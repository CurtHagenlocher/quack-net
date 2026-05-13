using System.Buffers.Binary;
using Quack;
using Quack.Data;
using Quack.Types;

namespace Quack.IntegrationTests;

public class SmokeTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public SmokeTests(QuackServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Connect_GetsValidSessionId()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);
        Assert.False(string.IsNullOrEmpty(conn.ConnectionId));
        Assert.Equal(1UL, conn.QuackVersion);
        Assert.False(string.IsNullOrEmpty(conn.ServerDuckDbVersion));
    }

    [Fact]
    public async Task Connect_WrongToken_Throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using QuackConnection _ = await QuackConnection.OpenAsync(_server.QuackUri, "wrong-token");
        });
    }

    [Fact]
    public async Task Execute_SelectIntegerLiteral_ReturnsExpectedValue()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        QuackQueryResult result = await conn.ExecuteAsync("SELECT 42::INTEGER AS x");

        Assert.Single(result.ColumnTypes);
        Assert.Equal(LogicalTypeId.Integer, result.ColumnTypes[0].Id);
        Assert.Equal("x", result.ColumnNames[0]);

        IReadOnlyList<DuckDbChunk> chunks = await result.ToListAsync();
        Assert.Single(chunks);
        DuckDbChunk chunk = chunks[0];
        Assert.Equal(1, chunk.RowCount);

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunk.Columns[0]);
        Assert.False(col.IsNull(0));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(0)));
    }

    [Fact]
    public async Task Execute_MultipleRows_ReturnsAllValues()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        QuackQueryResult result = await conn.ExecuteAsync(
            "SELECT i::INTEGER AS i FROM range(1, 6) t(i)");

        IReadOnlyList<DuckDbChunk> chunks = await result.ToListAsync();
        Assert.NotEmpty(chunks);
        int totalRows = chunks.Sum(c => c.RowCount);
        Assert.Equal(5, totalRows);

        FixedSizeColumn col = Assert.IsType<FixedSizeColumn>(chunks[0].Columns[0]);
        Assert.Equal(LogicalTypeId.Integer, col.Type.Id);
        // First chunk should hold all 5 rows since they fit in one chunk.
        for (int row = 0; row < chunks[0].RowCount; row++)
        {
            Assert.Equal(row + 1, BinaryPrimitives.ReadInt32LittleEndian(col.GetBytes(row)));
        }
    }

    [Fact]
    public async Task Execute_SelectVarchar_ReturnsStringValue()
    {
        await using QuackConnection conn = await QuackConnection.OpenAsync(_server.QuackUri, _server.Token);

        QuackQueryResult result = await conn.ExecuteAsync("SELECT 'hello world' AS s");

        Assert.Equal(LogicalTypeId.Varchar, result.ColumnTypes[0].Id);
        IReadOnlyList<DuckDbChunk> chunks = await result.ToListAsync();

        VarBytesColumn col = Assert.IsType<VarBytesColumn>(chunks[0].Columns[0]);
        Assert.Equal("hello world"u8.ToArray(), col.Values[0]!.Value.ToArray());
    }
}
