// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Quack.Protocol;
using Quack.Transport;

namespace Quack;

// Sends a request that belongs to a query (PREPARE, FETCH, SEND_DATA) so that
// a fired CancellationToken stops the QUERY rather than the HTTP request:
// the client sends a CancelRequest for the query, the server interrupts it,
// the pending request ends with the interrupt error, and the caller gets an
// OperationCanceledException with the transport still healthy, so the
// connection stays usable.
//
// Only if the request has not ended within the grace period (a server that
// does not answer) is the HTTP request aborted, which marks the transport
// broken as before.
internal static class QueryCancellation
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CancelRetryInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<TResponse> SendAsync<TResponse>(
        QuackTransport transport,
        QuackMessage request,
        Int128 queryUuid,
        CancellationToken cancellationToken,
        bool queryStarted,
        TimeSpan? grace = null)
        where TResponse : QuackMessage
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await transport.SendAsync<TResponse>(request, CancellationToken.None).ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            // A PREPARE that was never sent has nothing to stop; a later request
            // of a running query (FETCH, SEND_DATA) still stops the query.
            if (queryStarted)
            {
                await TrySendCancelAsync(transport, request.ConnectionId, queryUuid, DateTime.UtcNow + (grace ?? DefaultGrace)).ConfigureAwait(false);
            }
            throw new OperationCanceledException(cancellationToken);
        }

        using CancellationTokenSource abort = new();
        Task<TResponse> send = transport.SendAsync<TResponse>(request, abort.Token);
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), fired))
        {
            if (await Task.WhenAny(send, fired.Task).ConfigureAwait(false) == send)
            {
                return await send.ConfigureAwait(false);
            }
        }

        // The token fired while the request runs. Ask the server to interrupt
        // the query, and repeat until the request ends, WITHOUT waiting for the
        // previous CANCEL: one that arrives before the server has registered
        // the query is refused, and one that arrives after registration but
        // before the query starts executing can hang (quack 3bc63c0 interrupts
        // the connection, the starting query clears the flag, and the CANCEL
        // then joins a query that runs on). The next CANCEL interrupts the
        // running query and releases both. Each CANCEL names this query, so a
        // late one cannot hit a later query.
        DateTime deadline = DateTime.UtcNow + (grace ?? DefaultGrace);
        while (!send.IsCompleted && DateTime.UtcNow < deadline)
        {
            _ = TrySendCancelAsync(transport, request.ConnectionId, queryUuid, deadline);
            await Task.WhenAny(send, Task.Delay(CancelRetryInterval)).ConfigureAwait(false);
        }
        if (!send.IsCompleted)
        {
            // The server did not end the request: give up on this transport.
            await abort.CancelAsync().ConfigureAwait(false);
        }
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The interrupt error (or the local abort) is the expected outcome.
        }
        // Even a request that still succeeded is answered by cancellation: the
        // CANCEL has closed the query's result on the server.
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task TrySendCancelAsync(QuackTransport transport, string connectionId, Int128 queryUuid, DateTime deadline)
    {
        if (transport.IsBroken)
        {
            return;
        }
        TimeSpan left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            return;
        }
        using CancellationTokenSource timeout = new(left);
        try
        {
            await transport.SendSideRequestAsync<SuccessResponse>(
                new CancelRequestMessage { ConnectionId = connectionId, QueryUuid = queryUuid },
                timeout.Token).ConfigureAwait(false);
        }
        catch (QuackException)
        {
            // Refused (the query is not registered yet, or already gone); retried by the caller.
        }
        catch (OperationCanceledException)
        {
            // The deadline passed while cancelling; abandoned without breaking the transport.
        }
    }
}
