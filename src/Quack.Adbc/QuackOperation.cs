// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow.Adbc;

namespace Quack.Adbc;

// One running driver call (a query and its result stream, an ingest, a
// metadata query), registered with its connection so that StatementCancel
// (its owner) and ConnectionCancel (all) can stop it from another thread.
// Its token fires on a cancel or on the command timeout; the quack layer then
// cancels the query on the server.
internal sealed class QuackOperation : IDisposable
{
    private readonly QuackAdbcConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _cancelRequested;

    public QuackOperation(QuackAdbcConnection connection, object? owner, TimeSpan? timeout)
    {
        _connection = connection;
        Owner = owner;
        Timeout = timeout;
        if (timeout is not null)
        {
            _cts.CancelAfter(timeout.Value);
        }
    }

    public object? Owner { get; }

    public TimeSpan? Timeout { get; }

    public CancellationToken Token => _cts.Token;

    public bool WasCancelled => _cancelRequested;

    public bool TimedOut => _cts.IsCancellationRequested && !_cancelRequested;

    // Called by the connection, under its lock (so never after Dispose).
    internal void Cancel()
    {
        _cancelRequested = true;
        _cts.Cancel();
    }

    // The timeout covers getting a result, not reading it: a query's
    // operation stops its timer once the first response is in.
    public void StopTimeout()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    // The exception for an OperationCanceledException this operation caused.
    public Exception Translate(string operation) => _cancelRequested
        ? new AdbcException($"{operation} was cancelled.", AdbcStatusCode.Cancelled)
        : new TimeoutException(
            $"{operation} exceeded command_timeout_seconds={Timeout!.Value.TotalSeconds:0.###}; "
            + (_connection.Underlying.IsUsable
                ? "the query was cancelled on the server and the ADBC connection remains usable."
                : "the server did not stop the query in time and the ADBC connection is no longer usable."));

    public void Dispose()
    {
        if (_connection.EndOperation(this))
        {
            _cts.Dispose();
        }
    }
}
