// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// Server errors as ADBC statuses (DuckDB's exception type), with the
// exception type as an error detail; the quack:// URL form with token and tls.
public class ErrorStatusTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public ErrorStatusTests(QuackServerFixture server) => _server = server;

    [Fact]
    public void ServerErrors_MapToAdbcStatuses()
    {
        using AdbcConnection conn = Open(_server.QuackUri, _server.Token);
        string t = "es_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TABLE {t} (i INTEGER PRIMARY KEY)");
        Execute(conn, $"INSERT INTO {t} VALUES (1)");
        Assert.Equal(AdbcStatusCode.AlreadyExists, Status(conn, $"CREATE TABLE {t} (i INTEGER)"));
        Assert.Equal(AdbcStatusCode.NotFound, Status(conn, "SELECT * FROM no_such_table_here"));
        Assert.Equal(AdbcStatusCode.IntegrityError, Status(conn, $"INSERT INTO {t} VALUES (1)"));
        Assert.Equal(AdbcStatusCode.InvalidArgument, Status(conn, "SELEC 1"));
        Assert.Equal(AdbcStatusCode.InvalidData, Status(conn, "SELECT 'x'::INTEGER"));

        AdbcException e = Assert.ThrowsAny<AdbcException>(() => Execute(conn, "SELECT * FROM no_such_table_here"));
        KeyValuePair<string, byte[]> type = Assert.Single(e.Details!, d => d.Key == "quack.exception_type");
        Assert.Equal("Catalog", Encoding.UTF8.GetString(type.Value));
        Assert.IsType<QuackException>(e.InnerException);
        Execute(conn, $"DROP TABLE {t}");
    }

    // ADBC: an append whose data does not fit the table is ALREADY_EXISTS, a missing table NOT_FOUND.
    [Fact]
    public void AppendSchemaMismatch_IsAlreadyExists()
    {
        using AdbcConnection conn = Open(_server.QuackUri, _server.Token);
        string t = "am_" + Guid.NewGuid().ToString("N")[..8];
        Execute(conn, $"CREATE TABLE {t} (a INTEGER, b INTEGER)");
        Schema schema = new Schema.Builder().Field(new Field("a", Int32Type.Default, true)).Build();
        RecordBatch Batch() => new(schema, [new Int32Array.Builder().Append(1).Build()], 1);
        Assert.Equal(AdbcStatusCode.AlreadyExists, IngestStatus(conn, t, Batch()));
        Assert.Equal(AdbcStatusCode.NotFound, IngestStatus(conn, t + "_missing", Batch()));
        Execute(conn, $"DROP TABLE {t}");
    }

    // quack://host:port/?token=...&tls=false, as the other quack ADBC drivers take it.
    [Fact]
    public void UrlForm_CarriesTheToken()
    {
        string host = _server.QuackUri["quack:".Length..];
        using AdbcConnection conn = Open($"quack://{host}/?token={Uri.EscapeDataString(_server.Token)}&tls=false", token: null);
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = "SELECT 1";
        Assert.Equal(-1, stmt.ExecuteUpdate().AffectedRows);
        Assert.Equal(AdbcStatusCode.InvalidArgument,
            Assert.ThrowsAny<AdbcException>(() => Open($"quack://{host}/?tls=sometimes", _server.Token)).Status);
    }

    private static AdbcStatusCode Status(AdbcConnection conn, string sql)
        => Assert.ThrowsAny<AdbcException>(() => Execute(conn, sql)).Status;

    private static AdbcStatusCode IngestStatus(AdbcConnection conn, string table, RecordBatch batch)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SetOption("adbc.ingest.target_table", table);
        stmt.SetOption("adbc.ingest.mode", "adbc.ingest.mode.append");
        stmt.Bind(batch, batch.Schema);
        return Assert.ThrowsAny<AdbcException>(() => stmt.ExecuteUpdate()).Status;
    }

    private static AdbcConnection Open(string uri, string? token)
    {
        Dictionary<string, string> options = new() { [QuackAdbcDriver.UriParameter] = uri };
        if (token is not null)
        {
            options[QuackAdbcDriver.TokenParameter] = token;
        }
        return new QuackAdbcDriver().Open(options).Connect(options: null);
    }

    private static void Execute(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        stmt.ExecuteUpdate();
    }
}
