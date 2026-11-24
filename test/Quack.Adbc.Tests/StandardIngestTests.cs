// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// ADBC's standard bulk ingest: adbc.ingest.* options with Bind or BindStream.
public class StandardIngestTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public StandardIngestTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task Create_FromAStream_TakesNamesAndTypes()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        long affected = Ingest(conn, table, new BatchStream(schema, [IdName(schema, 0, 3), IdName(schema, 3, 0), IdName(schema, 3, 2)]));
        Assert.Equal(5, affected);
        Assert.Equal("id BIGINT, name VARCHAR", await Text(conn,
            $"SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY column_index) FROM duckdb_columns() WHERE table_name = '{table}'"));
        Assert.Equal("0:n0,1:n1,2:n2,3:n3,4:n4", await Text(conn, $"SELECT string_agg(id || ':' || name, ',' ORDER BY id) FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task Create_IsTheDefault_AndRefusesAnExistingTable()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        Ingest(conn, table, IdName(schema, 0, 2));
        Assert.ThrowsAny<Exception>(() => Ingest(conn, table, IdName(schema, 10, 2)));
        Assert.Equal("2", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task Append_NeedsTheTable()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        Assert.ThrowsAny<Exception>(() => Ingest(conn, table, IdName(schema, 0, 2), mode: "adbc.ingest.mode.append"));
        Ingest(conn, table, IdName(schema, 0, 2));
        Assert.Equal(3, Ingest(conn, table, IdName(schema, 2, 3), mode: "adbc.ingest.mode.append"));
        Assert.Equal("5", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task Replace_SwapsTheTable()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Execute(conn, $"CREATE TABLE {table} AS SELECT 'old' AS other FROM range(10)");
        Schema schema = IdNameSchema();
        Assert.Equal(2, Ingest(conn, table, IdName(schema, 0, 2), mode: "adbc.ingest.mode.replace"));
        Assert.Equal("2|0", await Text(conn, $"SELECT count(*) || '|' || min(id) FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task CreateAppend_CreatesOnce_ThenAppends()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        Ingest(conn, table, IdName(schema, 0, 2), mode: "adbc.ingest.mode.create_append");
        Ingest(conn, table, IdName(schema, 2, 2), mode: "adbc.ingest.mode.create_append");
        Assert.Equal("0,1,2,3", await Text(conn, $"SELECT string_agg(id::VARCHAR, ',' ORDER BY id) FROM {table}"));
        Assert.Equal("id BIGINT, name VARCHAR", await Text(conn,
            $"SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY column_index) FROM duckdb_columns() WHERE table_name = '{table}'"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task CatalogAndSchema_AreTheTarget()
    {
        using AdbcConnection conn = OpenConnection();
        string schemaName = "s_" + NewName();
        string table = NewName();
        Execute(conn, $"CREATE SCHEMA {schemaName}");
        Schema schema = IdNameSchema();
        Ingest(conn, table, IdName(schema, 0, 2), catalog: "memory", dbSchema: schemaName);
        Assert.Equal("2", await Text(conn, $"SELECT count(*)::VARCHAR FROM memory.{schemaName}.{table}"));
        Execute(conn, $"DROP SCHEMA {schemaName} CASCADE");
    }

    [Fact]
    public async Task Temporary_GoesToTempMain()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Execute(conn, $"CREATE TABLE {table} AS SELECT 42::BIGINT AS id, 'persistent' AS name");
        Schema schema = IdNameSchema();
        Ingest(conn, table, IdName(schema, 0, 3), temporary: true);
        Ingest(conn, table, IdName(schema, 3, 1), temporary: true, mode: "adbc.ingest.mode.append");
        Assert.Equal("4", await Text(conn, $"SELECT count(*)::VARCHAR FROM temp.main.{table}"));
        Assert.Equal("persistent", await Text(conn, $"SELECT name FROM memory.main.{table}"));
        AdbcException e = Assert.Throws<AdbcException>(() => Ingest(conn, table, IdName(schema, 0, 1), temporary: true, dbSchema: "main"));
        Assert.Equal(AdbcStatusCode.InvalidArgument, e.Status);
        Execute(conn, $"DROP TABLE temp.main.{table}");
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public async Task EmptyStream_StillCreatesTheTable()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        Assert.Equal(0, Ingest(conn, table, new BatchStream(schema, [])));
        Assert.Equal("0", await Text(conn, $"SELECT count(*)::VARCHAR FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    // Many batches, more than one SEND_DATA request: every row once, in stream order.
    [Fact]
    public async Task LargeStream_KeepsEveryRowInOrder()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = IdNameSchema();
        const int perBatch = 5000, batches = 120;
        long affected = Ingest(conn, table, new BatchStream(schema, Enumerable.Range(0, batches).Select(b => IdName(schema, b * perBatch, perBatch))));
        Assert.Equal(perBatch * batches, affected);
        Assert.Equal($"{perBatch * batches}|true", await Text(conn, $"SELECT count(*) || '|' || bool_and(id = rowid) FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    // Each batch is released by the driver, and only after it was sent: its
    // buffers poison themselves when released, so an early release shows up as
    // wrong values in the table.
    [Fact]
    public async Task StreamBatches_AreReleasedAfterTheyAreSent()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        Schema schema = new Schema.Builder().Field(new Field("v", Int64Type.Default, false)).Build();
        PoisoningAllocator allocator = new();
        List<PoisoningOwner> owners = allocator.Owners;
        IEnumerable<RecordBatch> Batches()
        {
            for (int b = 0; b < 40; b++)
            {
                Int64Array.Builder values = new();
                for (int i = 0; i < 1000; i++) values.Append(b * 1000 + i);
                yield return new RecordBatch(schema, [values.Build(allocator)], 1000);
            }
        }
        BatchStream stream = new(schema, Batches());
        Assert.Equal(40_000, Ingest(conn, table, stream));
        Assert.True(stream.Disposed, "the stream was not released");
        Assert.True(owners.Count >= 40, $"{owners.Count} buffers from the allocator");
        Assert.All(owners, o => Assert.True(o.Disposed, "a batch was not released"));
        Assert.Equal("40000|0|39999|799980000", await Text(conn, $"SELECT count(*) || '|' || min(v) || '|' || max(v) || '|' || sum(v) FROM {table}"));
        Execute(conn, $"DROP TABLE {table}");
    }

    [Theory]
    [InlineData("adbc.ingest.mode", "adbc.ingest.mode.upsert")]
    [InlineData("adbc.ingest.temporary", "yes")]
    public void InvalidOption_IsRefused(string key, string value)
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        AdbcException e = Assert.Throws<AdbcException>(() => stmt.SetOption(key, value));
        Assert.Equal(AdbcStatusCode.InvalidArgument, e.Status);
    }

    [Fact]
    public void TargetWithoutData_IsInvalidState()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SetOption("adbc.ingest.target_table", NewName());
        AdbcException e = Assert.Throws<AdbcException>(() => stmt.ExecuteUpdate());
        Assert.Equal(AdbcStatusCode.InvalidState, e.Status);
    }

    private static long Ingest(AdbcConnection conn, string table, object data, string? mode = null, string? catalog = null, string? dbSchema = null, bool temporary = false)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SetOption("adbc.ingest.target_table", table);
        if (mode is not null) stmt.SetOption("adbc.ingest.mode", mode);
        if (catalog is not null) stmt.SetOption("adbc.ingest.target_catalog", catalog);
        if (dbSchema is not null) stmt.SetOption("adbc.ingest.target_db_schema", dbSchema);
        if (temporary) stmt.SetOption("adbc.ingest.temporary", "true");
        if (data is RecordBatch batch)
        {
            stmt.Bind(batch, batch.Schema);
        }
        else
        {
            stmt.BindStream((IArrowArrayStream)data);
        }
        return stmt.ExecuteUpdate().AffectedRows;
    }

    private static Schema IdNameSchema() => new Schema.Builder()
        .Field(new Field("id", Int64Type.Default, true))
        .Field(new Field("name", StringType.Default, true))
        .Build();

    private static RecordBatch IdName(Schema schema, int start, int count)
    {
        Int64Array.Builder ids = new();
        StringArray.Builder names = new();
        for (int i = start; i < start + count; i++)
        {
            ids.Append(i);
            names.Append("n" + i);
        }
        return new RecordBatch(schema, [ids.Build(), names.Build()], count);
    }

    private static string NewName() => "si_" + Guid.NewGuid().ToString("N")[..8];

    private sealed class BatchStream(Schema schema, IEnumerable<RecordBatch> batches) : IArrowArrayStream
    {
        private readonly IEnumerator<RecordBatch> _batches = batches.GetEnumerator();

        public bool Disposed { get; private set; }

        public Schema Schema { get; } = schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
            => new(_batches.MoveNext() ? _batches.Current : null);

        public void Dispose()
        {
            Disposed = true;
            _batches.Dispose();
        }
    }

    private sealed class PoisoningAllocator : Apache.Arrow.Memory.MemoryAllocator
    {
        public List<PoisoningOwner> Owners { get; } = [];

        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            PoisoningOwner owner = new(length);
            Owners.Add(owner);
            bytesAllocated = length;
            return owner;
        }
    }

    private sealed class PoisoningOwner(int length) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[length];

        public bool Disposed { get; private set; }

        public Memory<byte> Memory => _bytes;

        public void Dispose()
        {
            System.Array.Fill(_bytes, (byte)0xEE);
            Disposed = true;
        }
    }

    private AdbcConnection OpenConnection()
    {
        QuackAdbcDriver driver = new();
        AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        });
        return db.Connect(options: null);
    }

    private static void Execute(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        stmt.ExecuteUpdate();
    }

    private static async Task<string> Text(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        QueryResult result = stmt.ExecuteQuery();
        using IArrowArrayStream stream = result.Stream!;
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        return ((StringArray)batch!.Column(0)).GetString(0);
    }
}
