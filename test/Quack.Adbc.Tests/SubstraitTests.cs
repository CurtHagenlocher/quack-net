// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;

namespace Quack.Adbc.Tests;

// A Substrait plan on a statement runs as the DuckDB query it translates to. The plans are real ones: produced by
// the fabricator extension's to_substrait for a DuckDB client pushing these queries to a quack server, over the two
// tables below (see Substrait/DuckDbSubstrait.cs).
public class SubstraitTests : IClassFixture<QuackServerFixture>
{
    private const string Tables =
        "CREATE OR REPLACE TABLE ed_t (id INTEGER PRIMARY KEY, i INTEGER, j INTEGER, b BIGINT, ti UTINYINT, si SMALLINT, " +
        "d DECIMAL(10,2), d1 DECIMAL(1,1), f DOUBLE, s VARCHAR, v VARCHAR, c VARCHAR, flag BOOLEAN, dt DATE, ts TIMESTAMP, g INTEGER); " +
        "INSERT INTO ed_t VALUES " +
        "(1, 7, 2, 9000000000, 5, 300, 2.50, 0.5, 2.5, 'Abc', 'Abc', 'ab', true, '2024-01-31', '2024-01-31 10:00:00', 1), " +
        "(2, -7, 2, -1, 0, -300, -2.50, -0.5, -2.5, 'abc', 'abc', 'ab  ', false, '2024-02-29', '2024-02-29 23:59:59.999999', 1), " +
        "(3, 5, 0, 2147483647, 255, 32767, 0.00, 0.0, 0.0, 'abc  ', 'abc  ', 'AB', NULL, '2023-12-31', '1970-01-01 00:00:00', 2), " +
        "(4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 2), " +
        "(5, 2147483647, 1, 2147483647, 1, 1, 1.49, 0.1, -0.0, 'École', 'Ecole', 'x', true, '2024-03-15', '2024-03-15 12:30:45.5', 3), " +
        "(6, 3, -2, 7, 2, 2, 3.56, 0.9, 1e308, '', '', '', false, '0001-01-01', '0001-01-01 00:00:00', 3), " +
        "(7, 3, 3, 7, 2, 2, 1.01, 0.3, 0.1, 'a%b', 'a_b', 'a', true, '9999-12-31', '9999-12-31 23:59:59', NULL); " +
        "CREATE OR REPLACE TABLE ed_u (id INTEGER PRIMARY KEY, i INTEGER, s VARCHAR); " +
        "INSERT INTO ed_u VALUES (1, 7, 'Abc'), (2, 7, 'abc'), (3, NULL, 'x'), (4, 5, NULL), (5, 3, 'abc  ')";

    // SELECT g, sum(i) AS total, count(*) AS n FROM ed_t WHERE i > 0 GROUP BY g
    private static readonly string AggregatePlan =
        "EhIaEBABGgpndDphbnlfYW55IAESDxoNEAIaB3N1bTpkZWMgAhIOGgwQAxoGY291bnQ6IAMa5AIS4QIK0gIqzwISvgIiuwIS8wES" +
        "8AESzQEKygESqwEKAmlkCgFpCgFqCgFiCgJ0aQoCc2kKAWQKAmQxCgFmCgFzCgF2CgFjCgRmbGFnCgJkdAoCdHMKAWcScAoEKgIQ" +
        "AQoEKgIQAQoEKgIQAQoEOgIQAQoEGgIQAQoEGgIQAQoJwgEGCAIQCiABCgnCAQYIARABIAEKBFoCEAEKBGICEAEKBGICEAEKBGIC" +
        "EAEKBAoCEAEKBYIBAhABCgeKAgQIBhgBCgQqAhABGAIiDAoICgIIAQoCCA8QAToMCgRtYWluCgRlZF90Gh4aHAgBGgQKAhABIgoa" +
        "CBIGCgISACIAIgYaBAoCKAAaAxIBACImCiQIAioHwgEEECYgAToXGhVaEwoHwgEEECYgARIIEgYKAhIAIgAiCgoICAMqBDoCEAEq" +
        "ChIICgQSAggBIgAaDAoIEgYKAhIAIgAQAhICYzASAmMxEgJjMjIKEGYqBkR1Y2tEQkIvCAESK2V4dGVuc2lvbjppby5zdWJzdHJh" +
        "aXQ6ZnVuY3Rpb25zX2NvbXBhcmlzb25CNwgCEjNleHRlbnNpb246aW8uc3Vic3RyYWl0OmZ1bmN0aW9uc19hcml0aG1ldGljX2Rl" +
        "Y2ltYWxCNggDEjJleHRlbnNpb246aW8uc3Vic3RyYWl0OmZ1bmN0aW9uc19hZ2dyZWdhdGVfZ2VuZXJpYw==";

    // SELECT t.id, u.s FROM ed_t t JOIN ed_u u ON t.i = u.i WHERE t.id < 5
    private static readonly string JoinPlan =
        "EhIaEBABGgpsdDphbnlfYW55IAESFRoTEAIaDWVxdWFsOmFueV9hbnkgARrrAxLoAwrdAyraAxK5Azq2AwoGEgQKAgEDEqEDOp4D" +
        "CggSBgoEBAUGBxLjAjLgAhLzARLwARLLAQrIARKrAQoCaWQKAWkKAWoKAWIKAnRpCgJzaQoBZAoCZDEKAWYKAXMKAXYKAWMKBGZs" +
        "YWcKAmR0CgJ0cwoBZxJwCgQqAhABCgQqAhABCgQqAhABCgQ6AhABCgQaAhABCgQaAhABCgnCAQYIAhAKIAEKCcIBBggBEAEgAQoE" +
        "WgIQAQoEYgIQAQoEYgIQAQoEYgIQAQoECgIQAQoFggECEAEKB4oCBAgGGAEKBCoCEAEYAiIKCgYKAggBCgAQAToMCgRtYWluCgRl" +
        "ZF90GiAaHggBGgQKAhABIgwaChIICgQSAggBIgAiBhoECgIoBRpACj4SIAoCaWQKAWkKAXMSFAoEKgIQAQoEKgIQAQoEYgIQARgC" +
        "IgwKCAoCCAEKAggCEAE6DAoEbWFpbgoEZWRfdSIkGiIIAhoECgIQASIKGggSBgoCEgAiACIMGgoSCAoEEgIIAiIAMAEaCBIGCgIS" +
        "ACIAGgoSCAoEEgIIASIAGgoSCAoEEgIIAiIAGgoSCAoEEgIIAyIAGggSBgoCEgAiABoMCggSBgoCEgAiABACGg4KChIICgQSAggB" +
        "IgAQAhICYzASAmMxMgoQZioGRHVja0RCQi8IARIrZXh0ZW5zaW9uOmlvLnN1YnN0cmFpdDpmdW5jdGlvbnNfY29tcGFyaXNvbg==";

    // SELECT g, any_value(s) FROM ed_t GROUP BY g: any_value stands for DuckDB's first and any_value alike
    private static readonly string AnyValuePlan =
        "EhUaExABGg1hbnlfdmFsdWU6YW55IAEaigIShwIK/AEi+QESzQEKygESqwEKAmlkCgFpCgFqCgFiCgJ0aQoCc2kKAWQKAmQxCgFm" +
        "CgFzCgF2CgFjCgRmbGFnCgJkdAoCdHMKAWcScAoEKgIQAQoEKgIQAQoEKgIQAQoEOgIQAQoEGgIQAQoEGgIQAQoJwgEGCAIQCiAB" +
        "CgnCAQYIARABIAEKBFoCEAEKBGICEAEKBGICEAEKBGICEAEKBAoCEAEKBYIBAhABCgeKAgQIBhgBCgQqAhABGAIiDAoICgIIDwoC" +
        "CAkQAToMCgRtYWluCgRlZF90GgMSAQAiGAoWCAEqBGICEAE6DBoKEggKBBICCAEiACoIEgYKAhIAIgASAmMwEgJjMTIKEGYqBkR1" +
        "Y2tEQkI2CAESMmV4dGVuc2lvbjppby5zdWJzdHJhaXQ6ZnVuY3Rpb25zX2FnZ3JlZ2F0ZV9nZW5lcmlj";

    // DELETE FROM ed_t WHERE i > 5
    private static readonly string DeletePlan =
        "EhIaEBABGgpndDphbnlfYW55IAEaowISoAIKnQKaAZkCCgwKBG1haW4KBGVkX3QaACACKoICOv8BCgUSAwoBARLvARLsARLJAQrG" +
        "ARKrAQoCaWQKAWkKAWoKAWIKAnRpCgJzaQoBZAoCZDEKAWYKAXMKAXYKAWMKBGZsYWcKAmR0CgJ0cwoBZxJwCgQqAhABCgQqAhAB" +
        "CgQqAhABCgQ6AhABCgQaAhABCgQaAhABCgnCAQYIAhAKIAEKCcIBBggBEAEgAQoEWgIQAQoEYgIQAQoEYgIQAQoEYgIQAQoECgIQ" +
        "AQoFggECEAEKB4oCBAgGGAEKBCoCEAEYAiIICgQKAggBEAE6DAoEbWFpbgoEZWRfdBoeGhwIARoECgIQASIKGggSBgoCEgAiACIG" +
        "GgQKAigFGgQKAigBMAEyChBmKgZEdWNrREJCLwgBEitleHRlbnNpb246aW8uc3Vic3RyYWl0OmZ1bmN0aW9uc19jb21wYXJpc29u";

    // UPDATE ed_t SET s = upper(s), i = i * 2 WHERE g = 1
    private static readonly string UpdatePlan =
        "EhUaExABGg1lcXVhbDphbnlfYW55IAESERoPEAIaCXVwcGVyOnN0ciACEhgaFhADGhBtdWx0aXBseTppMzJfaTMyIAMa6AIS5QIK" +
        "4gKaAd4CCgwKBG1haW4KBGVkX3QaBgoBcwoBaSADKsECOr4CCgYSBAoCAwQS9wES9AES0QEKzgESqwEKAmlkCgFpCgFqCgFiCgJ0" +
        "aQoCc2kKAWQKAmQxCgFmCgFzCgF2CgFjCgRmbGFnCgJkdAoCdHMKAWcScAoEKgIQAQoEKgIQAQoEKgIQAQoEOgIQAQoEGgIQAQoE" +
        "GgIQAQoJwgEGCAIQCiABCgnCAQYIARABIAEKBFoCEAEKBGICEAEKBGICEAEKBGICEAEKBAoCEAEKBYIBAhABCgeKAgQIBhgBCgQq" +
        "AhABGAIiEAoMCgIIDwoCCAkKAggBEAE6DAoEbWFpbgoEZWRfdBoeGhwIARoECgIQASIKGggSBgoCEgAiACIGGgQKAigBGhgaFggC" +
        "GgRiAhABIgwaChIICgQSAggBIgAaIBoeCAMaBCoCEAEiDBoKEggKBBICCAIiACIGGgQKAigCMAEyChBmKgZEdWNrREJCLwgBEitl" +
        "eHRlbnNpb246aW8uc3Vic3RyYWl0OmZ1bmN0aW9uc19jb21wYXJpc29uQisIAhInZXh0ZW5zaW9uOmlvLnN1YnN0cmFpdDpmdW5j" +
        "dGlvbnNfc3RyaW5nQi8IAxIrZXh0ZW5zaW9uOmlvLnN1YnN0cmFpdDpmdW5jdGlvbnNfYXJpdGhtZXRpYw==";

    // SELECT t.id, x.one, x.y FROM ed_t t LEFT JOIN (SELECT id, 1 AS one, coalesce(i, 0) AS y FROM ed_u) x ON t.id = x.id
    // (ORDER BY t.id LIMIT 100): a constant on the side the join pads with NULL
    private static readonly string LeftJoinPlan =
        "EhQaEhABGgxjb2FsZXNjZTphbnkgARIVGhMQAhoNZXF1YWw6YW55X2FueSABGoMEEoAECvEDGu4DEt8DKtwDEssDOsgDCgcSBQoD" +
        "AAIDErIDOq8DCggSBgoEBAUGBxL0AjLxAhLHAQrEARKrAQoCaWQKAWkKAWoKAWIKAnRpCgJzaQoBZAoCZDEKAWYKAXMKAXYKAWMK" +
        "BGZsYWcKAmR0CgJ0cwoBZxJwCgQqAhABCgQqAhABCgQqAhABCgQ6AhABCgQaAhABCgQaAhABCgnCAQYIAhAKIAEKCcIBBggBEAEg" +
        "AQoEWgIQAQoEYgIQAQoEYgIQAQoEYgIQAQoECgIQAQoFggECEAEKB4oCBAgGGAEKBCoCEAEYAiIGCgIKABABOgwKBG1haW4KBGVk" +
        "X3QafTp7CgcSBQoDAAIDEj4KPBIgCgJpZAoBaQoBcxIUCgQqAhABCgQqAhABCgRiAhABGAIiCgoGCgAKAggBEAE6DAoEbWFpbgoE" +
        "ZWRfdRoECgIoARoqGigIARoEKgIQASIMGgoSCAoEEgIIASIAIhAaDloMCgQqAhABEgQKAigAIiQaIggCGgQKAhABIgoaCBIGCgIS" +
        "ACIAIgwaChIICgQSAggBIgAwAxoIEgYKAhIAIgAaChIICgQSAggBIgAaChIICgQSAggCIgAaChIICgQSAggDIgAaCBIGCgISACIA" +
        "GgwKCBIGCgISACIAEAIqBAoCOAAyBAoCOGQSAmMwEgJjMRICYzIyChBmKgZEdWNrREJCLwgBEitleHRlbnNpb246aW8uc3Vic3Ry" +
        "YWl0OmZ1bmN0aW9uc19jb21wYXJpc29u";

    // MERGE INTO ed_u t USING (SELECT id, s FROM ed_t) src ON t.id = src.id
    //   WHEN MATCHED THEN UPDATE SET s = src.s WHEN NOT MATCHED THEN INSERT (id, s) VALUES (src.id, src.s)
    private static readonly string MergePlan =
        "EhUaExABGg1lcXVhbDphbnlfYW55IAESFxoVEAIaD2lzX25vdF9udWxsOmFueSABEhAaDhADGghhbmQ6Ym9vbCACEhMaERAEGgtp" +
        "c19udWxsOmFueSABGt8IEtwICtkImgHVCAoMCgRtYWluCgRlZF91Gh4KBGs6aWQKAnRwCgJvcAoDdTpzCgRpOmlkCgNpOnMqjAg6" +
        "iQgKChIICgYDBgcIAAESmQUSlgUSywM6yAMKCxIJCgcHCAkKCwwNEuYCMuMCEuABOt0BCgcSBQoDAAECEssBCsgBEqsBCgJpZAoB" +
        "aQoBagoBYgoCdGkKAnNpCgFkCgJkMQoBZgoBcwoBdgoBYwoEZmxhZwoCZHQKAnRzCgFnEnAKBCoCEAEKBCoCEAEKBCoCEAEKBDoC" +
        "EAEKBBoCEAEKBBoCEAEKCcIBBggCEAogAQoJwgEGCAEQASABCgRaAhABCgRiAhABCgRiAhABCgRiAhABCgQKAhABCgWCAQIQAQoH" +
        "igIECAYYAQoEKgIQARgCIgoKBgoACgIICRABOgwKBG1haW4KBGVkX3QaBAoCKAEaVjpUCggSBgoEAAECAxJCCkASIAoCaWQKAWkK" +
        "AXMSFAoEKgIQAQoEKgIQAQoEYgIQARgCIg4KCgoACgIIAQoCCAIQAToMCgRtYWluCgRlZF91GgQKAigBIiQaIggBGgQKAhABIgoa" +
        "CBIGCgISACIAIgwaChIICgQSAggDIgAwAxoIEgYKAhIAIgAaChIICgQSAggBIgAaChIICgQSAggCIgAaChIICgQSAggDIgAaChII" +
        "CgQSAggEIgAaChIICgQSAggFIgAaChIICgQSAggGIgAaxQEawgEIAhoECgIQASK3ARq0ATKxAQpeCkIaQAgDGgQKAhABIhoaGBoW" +
        "CAIaBAoCEAEiDBoKEggKBBICCAYiACIaGhgaFggCGgQKAhABIgwaChIICgQSAggCIgASGFoWCgQqAhABEg5aDAoEKgIQARIECgIo" +
        "AQo0ChgaFggEGgQKAhABIgwaChIICgQSAggGIgASGFoWCgQqAhABEg5aDAoEKgIQARIECgIoAxIZWhcKBCoCEAESD1oNCgQqAhAB" +
        "EgUKA+oBABq0ATKxAQpeCkIaQAgDGgQKAhABIhoaGBoWCAIaBAoCEAEiDBoKEggKBBICCAYiACIaGhgaFggCGgQKAhABIgwaChII" +
        "CgQSAggCIgASGFoWCgQqAhABEg5aDAoEKgIQARIECgIoAQo0ChgaFggEGgQKAhABIgwaChIICgQSAggGIgASGFoWCgQqAhABEg5a" +
        "DAoEKgIQARIECgIoAxIZWhcKBCoCEAESD1oNCgQqAhABEgUKA+oBABqnATKkAQpaCkIaQAgDGgQKAhABIhoaGBoWCAIaBAoCEAEi" +
        "DBoKEggKBBICCAYiACIaGhgaFggCGgQKAhABIgwaChIICgQSAggCIgASFFoSCgRiAhABEgoSCAoEEgIIASIACjAKGBoWCAQaBAoC" +
        "EAEiDBoKEggKBBICCAYiABIUWhIKBGICEAESChIICgQSAggFIgASFFoSCgRiAhABEgoSCAoEEgIIBSIAMAFKFBISChBmYWJyaWNh" +
        "dG9yLm1lcmdlMgoQZioGRHVja0RCQi8IARIrZXh0ZW5zaW9uOmlvLnN1YnN0cmFpdDpmdW5jdGlvbnNfY29tcGFyaXNvbkIsCAIS" +
        "KGV4dGVuc2lvbjppby5zdWJzdHJhaXQ6ZnVuY3Rpb25zX2Jvb2xlYW4=";

    private readonly QuackServerFixture _server;

    public SubstraitTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task Aggregate_AnswersLikeTheSql()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        Assert.Equal(
            await Rows(conn, sql: "SELECT g, sum(i), count(*) FROM ed_t WHERE i > 0 GROUP BY g"),
            await Rows(conn, plan: AggregatePlan));
    }

    [Fact]
    public async Task Join_AnswersLikeTheSql()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        Assert.Equal(
            await Rows(conn, sql: "SELECT t.id, u.s FROM ed_t t JOIN ed_u u ON t.i = u.i WHERE t.id < 5"),
            await Rows(conn, plan: JoinPlan));
    }

    [Theory]
    [InlineData("delete", "DELETE FROM ed_t WHERE i > 5")]
    [InlineData("update", "UPDATE ed_t SET s = upper(s), i = i * 2 WHERE g = 1")]
    public async Task Write_ChangesWhatTheSqlChanges(string plan, string sql)
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        long viaSql = Update(conn, sql: sql);
        List<string> expected = await Rows(conn, sql: "SELECT id, i, g, s FROM ed_t");
        Execute(conn, Tables);
        long viaPlan = Update(conn, plan: plan == "delete" ? DeletePlan : UpdatePlan);
        Assert.Equal(2, viaPlan);
        Assert.Equal(viaSql, viaPlan);
        Assert.Equal(expected, await Rows(conn, sql: "SELECT id, i, g, s FROM ed_t"));
    }

    [Fact]
    public async Task LeftJoin_PadsComputedColumnsWithNull()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        List<string> rows = await Rows(conn, plan: LeftJoinPlan);
        Assert.Equal(
            await Rows(conn, sql: "SELECT t.id, x.one, x.y FROM ed_t t LEFT JOIN (SELECT id, 1 AS one, coalesce(i, 0) AS y FROM ed_u) x ON t.id = x.id"),
            rows);
        Assert.Contains("6|NULL|NULL", rows);
    }

    [Fact]
    public async Task Merge_ChangesWhatTheSqlChanges()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        long viaSql = Update(conn, sql: "MERGE INTO ed_u t USING (SELECT id, s FROM ed_t) src ON t.id = src.id "
            + "WHEN MATCHED THEN UPDATE SET s = src.s WHEN NOT MATCHED THEN INSERT (id, s) VALUES (src.id, src.s)");
        List<string> expected = await Rows(conn, sql: "SELECT id, i, s FROM ed_u");
        Execute(conn, Tables);
        long viaPlan = Update(conn, plan: MergePlan);
        Assert.Equal(viaSql, viaPlan);
        Assert.Equal(expected, await Rows(conn, sql: "SELECT id, i, s FROM ed_u"));
    }

    [Fact]
    public void Prepare_RefusesWhatItCannotWriteExactly()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SubstraitPlan = Convert.FromBase64String(AnyValuePlan);
        AdbcException e = Assert.Throws<AdbcException>(stmt.Prepare);
        Assert.Equal(AdbcStatusCode.NotImplemented, e.Status);
        Assert.Contains("any_value", e.Message);
    }

    [Fact]
    public void ExecuteSchema_OfAPlan()
    {
        using AdbcConnection conn = Open();
        Execute(conn, Tables);
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SubstraitPlan = Convert.FromBase64String(JoinPlan);
        stmt.Prepare();
        Schema schema = stmt.ExecuteSchema();
        Assert.Equal(new[] { "c0", "c1" }, schema.FieldsList.Select(f => f.Name));
    }

    [Fact]
    public async Task SqlQuery_ReplacesAPlan()
    {
        using AdbcConnection conn = Open();
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SubstraitPlan = Convert.FromBase64String(AnyValuePlan);
        stmt.SqlQuery = "SELECT 42 AS x";
        Assert.Null(stmt.SubstraitPlan);
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        RecordBatch batch = (await stream.ReadNextRecordBatchAsync())!;
        Assert.Equal(42, ((Int32Array)batch.Column(0)).GetValue(0));
    }

    private AdbcConnection Open()
    {
        Dictionary<string, string> options = new()
        {
            [QuackAdbcDriver.UriParameter] = _server.QuackUri,
            [QuackAdbcDriver.TokenParameter] = _server.Token,
        };
        return new QuackAdbcDriver().Open(options).Connect(options: null);
    }

    private static void Execute(AdbcConnection conn, string sql)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        stmt.SqlQuery = sql;
        stmt.ExecuteUpdate();
    }

    private static long Update(AdbcConnection conn, string? sql = null, string? plan = null)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        if (plan is not null)
        {
            stmt.SubstraitPlan = Convert.FromBase64String(plan);
            stmt.Prepare();
        }
        else
        {
            stmt.SqlQuery = sql;
        }
        return stmt.ExecuteUpdate().AffectedRows;
    }

    // The rows as text, sorted (neither query orders them).
    private static async Task<List<string>> Rows(AdbcConnection conn, string? sql = null, string? plan = null)
    {
        using AdbcStatement stmt = conn.CreateStatement();
        if (plan is not null)
        {
            stmt.SubstraitPlan = Convert.FromBase64String(plan);
        }
        else
        {
            stmt.SqlQuery = sql;
        }
        using IArrowArrayStream stream = stmt.ExecuteQuery().Stream!;
        var rows = new List<string>();
        while (await stream.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                for (int r = 0; r < batch.Length; r++)
                {
                    rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => Cell(batch.Column(c), r))));
                }
            }
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Cell(IArrowArray array, int row) => array.IsNull(row) ? "NULL" : array switch
    {
        Int32Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
        Int64Array a => a.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
        Decimal128Array a => a.GetString(row)!,
        StringArray a => a.GetString(row)!,
        _ => throw new NotSupportedException(array.GetType().Name),
    };
}
