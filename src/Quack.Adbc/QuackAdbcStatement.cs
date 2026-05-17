// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Quack.Data;

namespace Quack.Adbc;

internal sealed class QuackAdbcStatement : AdbcStatement
{
    private readonly QuackAdbcConnection _connection;
    private RecordBatch? _boundBatch;
    private TimeSpan? _commandTimeoutOverride;
    private bool _commandTimeoutOverrideSet;
    private bool _disposed;

    public QuackAdbcStatement(QuackAdbcConnection connection)
    {
        _connection = connection;
    }

    public override void Bind(RecordBatch batch, Schema schema)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(batch);
        // The schema is provided by callers for parameterized prepared
        // statements; we don't support those, so we just keep the batch.
        // The batch carries its own schema for the ingest path.
        _boundBatch?.Dispose();
        _boundBatch = batch;
    }

    public override void SetOption(string key, string value)
    {
        ThrowIfDisposed();
        if (string.Equals(key, QuackAdbcDriver.CommandTimeoutSecondsParameter, StringComparison.Ordinal))
        {
            _commandTimeoutOverride = string.IsNullOrEmpty(value)
                ? null
                : QuackAdbcDatabase.ParseCommandTimeoutSeconds(value);
            _commandTimeoutOverrideSet = true;
            return;
        }
        base.SetOption(key, value);
    }

    public override QueryResult ExecuteQuery()
    {
        ThrowIfDisposed();
        if (_boundBatch is not null)
        {
            throw new InvalidOperationException(
                "ExecuteQuery cannot be used with a bound RecordBatch; use ExecuteUpdate to ingest rows.");
        }
        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteQuery.");
        }

        using TimeoutScope scope = OpenTimeoutScope();
        QuackQueryResult result;
        try
        {
            result = _connection.Underlying
                .ExecuteAsync(SqlQuery, scope.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (scope.FiredByTimeout)
        {
            throw new TimeoutException(scope.TimeoutMessage("ExecuteQuery"));
        }
        return new QueryResult(-1, new QuackArrowArrayStream(result));
    }

    public override UpdateResult ExecuteUpdate()
    {
        ThrowIfDisposed();
        // Bound-batch path: append the batch's rows to the table named by
        // SqlQuery. ADBC's contract for ingest via Bind+ExecuteUpdate uses
        // SqlQuery as the target table identifier.
        if (_boundBatch is not null)
        {
            if (string.IsNullOrEmpty(SqlQuery))
            {
                throw new InvalidOperationException(
                    "SqlQuery (target table name) must be set when a RecordBatch is bound.");
            }
            DuckDbChunk chunk = RecordBatchConverter.ToDuckDbChunk(_boundBatch);
            using TimeoutScope appendScope = OpenTimeoutScope();
            try
            {
                _connection.Underlying
                    .AppendAsync(SqlQuery, chunk, appendScope.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (appendScope.FiredByTimeout)
            {
                throw new TimeoutException(appendScope.TimeoutMessage("ExecuteUpdate (append)"));
            }
            long appended = _boundBatch.Length;
            _boundBatch.Dispose();
            _boundBatch = null;
            return new UpdateResult(appended);
        }

        if (string.IsNullOrEmpty(SqlQuery))
        {
            throw new InvalidOperationException("SqlQuery must be set before calling ExecuteUpdate.");
        }

        // SQL-only path: execute and drain. quack doesn't return an affected-
        // row count, so we report -1 unless rows came back.
        using TimeoutScope scope = OpenTimeoutScope();
        long total = 0;
        try
        {
            QuackQueryResult result = _connection.Underlying
                .ExecuteAsync(SqlQuery, scope.Token)
                .GetAwaiter()
                .GetResult();
            foreach (DuckDbChunk chunk in result.ToListAsync(scope.Token).GetAwaiter().GetResult())
            {
                total += chunk.RowCount;
            }
        }
        catch (OperationCanceledException) when (scope.FiredByTimeout)
        {
            throw new TimeoutException(scope.TimeoutMessage("ExecuteUpdate"));
        }
        return new UpdateResult(total == 0 ? -1 : total);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _boundBatch?.Dispose();
            _boundBatch = null;
        }
        base.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackAdbcStatement));
        }
    }

    private TimeoutScope OpenTimeoutScope()
    {
        TimeSpan? effective = _commandTimeoutOverrideSet
            ? _commandTimeoutOverride
            : _connection.DefaultCommandTimeout;
        return new TimeoutScope(effective);
    }

    // Bundles the CTS lifecycle so callers can `using` it and ask whether
    // a thrown OperationCanceledException originated from the timeout.
    private readonly struct TimeoutScope : IDisposable
    {
        private readonly CancellationTokenSource? _cts;
        public TimeSpan? Timeout { get; }

        public TimeoutScope(TimeSpan? timeout)
        {
            Timeout = timeout;
            _cts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        }

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

        public bool FiredByTimeout => _cts is not null && _cts.IsCancellationRequested;

        public string TimeoutMessage(string operation)
            => $"{operation} exceeded command_timeout_seconds={Timeout!.Value.TotalSeconds:0.###}; the ADBC connection is no longer usable.";

        public void Dispose() => _cts?.Dispose();
    }
}
