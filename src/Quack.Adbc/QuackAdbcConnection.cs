using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;

namespace Quack.Adbc;

internal sealed class QuackAdbcConnection : AdbcConnection
{
    private readonly QuackConnection _connection;
    private bool _disposed;

    public QuackAdbcConnection(QuackConnection connection)
    {
        _connection = connection;
    }

    internal QuackConnection Underlying => _connection;

    public override AdbcStatement CreateStatement()
    {
        ThrowIfDisposed();
        return new QuackAdbcStatement(this);
    }

    public override IArrowArrayStream GetInfo(IReadOnlyList<AdbcInfoCode> codes)
        => throw AdbcException.NotImplemented("GetInfo is not yet implemented for quack-net.");

    public override IArrowArrayStream GetObjects(
        GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        IReadOnlyList<string>? tableTypes,
        string? columnNamePattern)
        => throw AdbcException.NotImplemented("GetObjects is not yet implemented for quack-net.");

    public override Schema GetTableSchema(string? catalog, string? dbSchema, string tableName)
        => throw AdbcException.NotImplemented("GetTableSchema is not yet implemented for quack-net.");

    public override IArrowArrayStream GetTableTypes()
        => throw AdbcException.NotImplemented("GetTableTypes is not yet implemented for quack-net.");

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QuackAdbcConnection));
        }
    }
}
