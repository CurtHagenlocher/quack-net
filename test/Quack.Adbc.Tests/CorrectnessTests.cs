// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Arrays;
using Apache.Arrow.Ipc;
using Apache.Arrow.Scalars;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// Rows affected, UUIDs and fixed-size binary on ingest, INTERVAL overflow.
public class CorrectnessTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public CorrectnessTests(QuackServerFixture server) => _server = server;

    [Fact]
    public void ExecuteUpdate_ReportsDuckDbsCount()
    {
        using AdbcConnection conn = OpenConnection();
        string t = "rc_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(-1, Update(conn, $"CREATE TABLE {t} (i INTEGER)"));
        Assert.Equal(3, Update(conn, $"INSERT INTO {t} VALUES (1), (2), (3)"));
        Assert.Equal(2, Update(conn, $"UPDATE {t} SET i = i + 10 WHERE i >= 2"));
        Assert.Equal(0, Update(conn, $"DELETE FROM {t} WHERE i = 99"));
        Assert.Equal(1, Update(conn, $"DELETE FROM {t} WHERE i = 1"));
        Assert.Equal(2, Update(conn, $"CREATE TABLE {t}_copy AS SELECT * FROM {t}"));
        Assert.Equal(-1, Update(conn, $"SELECT * FROM {t}"));
        Assert.Equal(-1, Update(conn, $"DROP TABLE {t}_copy"));
        Update(conn, $"DROP TABLE {t}");
    }

    // arrow.uuid goes in as a UUID (RFC byte order), any other fixed-size binary as a BLOB.
    [Fact]
    public async Task Ingest_ArrowUuid_And_FixedSizeBinary()
    {
        using AdbcConnection conn = OpenConnection();
        string t = "iu_" + Guid.NewGuid().ToString("N")[..8];
        Guid[] guids = [Guid.Parse("12345678-1234-1234-1234-123456789abc"), Guid.Parse("ffffffff-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002")];
        GuidArray.Builder uuids = new();
        foreach (Guid g in guids) uuids.Append(g);
        uuids.AppendNull();
        // Rows 0, 1 and 3 are valid (bitmap 0b1011); row 2 is null.
        FixedSizeBinaryArray fixedBytes = new(new ArrayData(new FixedSizeBinaryType(4), 4, 1, 0,
            [new ArrowBuffer(new byte[] { 0b1011 }), new ArrowBuffer(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0, 0, 255 })]));
        Schema schema = new Schema.Builder()
            .Field(new Field("u", GuidType.Default, true))
            .Field(new Field("b", new FixedSizeBinaryType(4), true))
            .Build();
        using (AdbcStatement ingest = conn.CreateStatement())
        {
            ingest.SetOption("adbc.ingest.target_table", t);
            ingest.Bind(new RecordBatch(schema, [uuids.Build(), fixedBytes], 4), schema);
            ingest.ExecuteUpdate();
        }
        Assert.Equal("u UUID, b BLOB", await Text(conn, $"SELECT string_agg(column_name || ' ' || data_type, ', ' ORDER BY column_index) FROM duckdb_columns() WHERE table_name = '{t}'"));
        Assert.Equal(string.Join(",", guids.Select(g => g.ToString("D"))) + ",NULL",
            await Text(conn, $"SELECT string_agg(coalesce(u::VARCHAR, 'NULL'), ',' ORDER BY rowid) FROM {t}"));
        Assert.Equal("01020304,05060708,NULL,000000FF",
            await Text(conn, $"SELECT string_agg(coalesce(hex(b), 'NULL'), ',' ORDER BY rowid) FROM {t}"));
        Update(conn, $"DROP TABLE {t}");
    }

    // micros * 1000 wraps like DuckDB's own Arrow export instead of failing.
    [Fact]
    public async Task Interval_WithHugeMicros_Wraps()
    {
        using AdbcConnection conn = OpenConnection();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT to_microseconds(9223372036854775) AS small_enough, to_microseconds(9223372036854776) AS wraps";
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        MonthDayNanosecondInterval fits = ((MonthDayNanosecondIntervalArray)batch.Column(0)).GetValue(0)!.Value;
        MonthDayNanosecondInterval wrapped = ((MonthDayNanosecondIntervalArray)batch.Column(1)).GetValue(0)!.Value;
        Assert.Equal(9223372036854775L * 1000, fits.Nanoseconds);
        Assert.Equal(unchecked(9223372036854776L * 1000), wrapped.Nanoseconds);
    }

    private static long Update(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        return stmt.ExecuteUpdate().AffectedRows;
    }

    private static async Task<string> Text(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        return ((StringArray)batch.Column(0)).GetString(0);
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
}
