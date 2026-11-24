// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

// ADBC transactions: autocommit off opens one on the quack session; Commit and
// Rollback end it and open the next.
public class TransactionTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public TransactionTests(QuackServerFixture server) => _server = server;

    [Fact]
    public void Autocommit_IsTheDefault_AndCommitNeedsItOff()
    {
        using AdbcConnection conn = OpenConnection();
        Assert.True(conn.AutoCommit);
        Assert.Equal("true", conn.GetOption("adbc.connection.autocommit"));
        Assert.Equal(AdbcStatusCode.InvalidState, Assert.Throws<AdbcException>(conn.Commit).Status);
        Assert.Equal(AdbcStatusCode.InvalidState, Assert.Throws<AdbcException>(conn.Rollback).Status);
    }

    [Fact]
    public void Commit_MakesTheChangesVisible_OnlyThen()
    {
        using AdbcConnection writer = OpenConnection();
        using AdbcConnection reader = OpenConnection();
        string table = NewName();
        Execute(writer, $"CREATE TABLE {table} (i INTEGER)");
        writer.SetOption("adbc.connection.autocommit", "false");
        Assert.Equal("false", writer.GetOption("adbc.connection.autocommit"));
        Execute(writer, $"INSERT INTO {table} VALUES (1), (2)");
        Assert.Equal(2, Count(writer, table));
        Assert.Equal(0, Count(reader, table));
        writer.Commit();
        Assert.Equal(2, Count(reader, table));

        // Still in a transaction after Commit.
        Execute(writer, $"INSERT INTO {table} VALUES (3)");
        Assert.Equal(2, Count(reader, table));
        writer.Rollback();
        Assert.Equal(2, Count(writer, table));
        writer.AutoCommit = true;
        Execute(writer, $"DROP TABLE {table}");
    }

    [Fact]
    public void Rollback_UndoesDdlAndIngest()
    {
        using AdbcConnection conn = OpenConnection();
        string table = NewName();
        conn.AutoCommit = false;
        using (AdbcStatement ingest = conn.CreateStatement())
        {
            ingest.SetOption("adbc.ingest.target_table", table);
            Int32Array values = new Int32Array.Builder().AppendRange([1, 2, 3]).Build();
            Schema schema = new Schema.Builder().Field(new Field("i", Int32Type.Default, true)).Build();
            ingest.Bind(new RecordBatch(schema, [values], 3), schema);
            Assert.Equal(3, ingest.ExecuteUpdate().AffectedRows);
        }
        Assert.Equal(3, Count(conn, table));
        conn.Rollback();
        Assert.Equal(0, CountTables(conn, table));
    }

    [Fact]
    public void AutocommitOn_CommitsTheOpenTransaction()
    {
        using AdbcConnection writer = OpenConnection();
        using AdbcConnection reader = OpenConnection();
        string table = NewName();
        Execute(writer, $"CREATE TABLE {table} (i INTEGER)");
        writer.AutoCommit = false;
        Execute(writer, $"INSERT INTO {table} VALUES (7)");
        Assert.Equal(0, Count(reader, table));
        writer.AutoCommit = true;
        Assert.Equal(1, Count(reader, table));
        Execute(writer, $"DROP TABLE {table}");
    }

    // With reconnect_on_session_loss on, a session lost inside a transaction is
    // not re-established behind the caller's back (the next statement would
    // run, and commit, outside the transaction): the call fails, and Rollback
    // starts over on a new session.
    [Fact]
    public async Task SessionLoss_InATransaction_FailsTheCall_AndRollbackRecovers()
    {
        using QuackAdbcDriver driver = new();
        using AdbcDatabase db = driver.Open(new Dictionary<string, string>
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
            [QuackAdbcDriver.ReconnectOnSessionLossParameter] = "true",
        });
        using AdbcConnection conn = db.Connect(options: null);
        using AdbcConnection reader = OpenConnection();
        string table = NewName();
        Execute(conn, $"CREATE TABLE {table} (i INTEGER)");
        conn.AutoCommit = false;
        Execute(conn, $"INSERT INTO {table} VALUES (1)");

        QuackConnection quack = ((QuackAdbcConnection)conn).Underlying;
        int session = quack.SessionGeneration;
        await quack.Transport.SendAsync<Quack.Protocol.SuccessResponse>(
            new Quack.Protocol.DisconnectMessage { ConnectionId = quack.ConnectionId }, CancellationToken.None);

        Assert.IsType<QuackSessionExpiredException>(Assert.ThrowsAny<Exception>(() => Execute(conn, $"INSERT INTO {table} VALUES (2)")));
        Assert.Equal(session, quack.SessionGeneration);
        conn.Rollback();
        Assert.NotEqual(session, quack.SessionGeneration);
        Execute(conn, $"INSERT INTO {table} VALUES (3)");
        conn.Commit();
        Assert.Equal(3, Scalar(reader, $"SELECT sum(i)::INTEGER FROM {table}"));
        Assert.Equal(1, Count(reader, table));
        conn.AutoCommit = true;
        Execute(conn, $"DROP TABLE {table}");
    }

    [Fact]
    public void InvalidAutocommitValue_IsRefused()
    {
        using AdbcConnection conn = OpenConnection();
        Assert.Equal(AdbcStatusCode.InvalidArgument,
            Assert.Throws<AdbcException>(() => conn.SetOption("adbc.connection.autocommit", "maybe")).Status);
    }

    private static int Count(AdbcConnection conn, string table) => Scalar(conn, $"SELECT count(*)::INTEGER FROM {table}");

    private static int CountTables(AdbcConnection conn, string table)
        => Scalar(conn, $"SELECT count(*)::INTEGER FROM duckdb_tables() WHERE table_name = '{table}'");

    private static int Scalar(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult()!;
        return ((Int32Array)batch.Column(0)).GetValue(0)!.Value;
    }

    private static string NewName() => "tx_" + Guid.NewGuid().ToString("N")[..8];

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
}
