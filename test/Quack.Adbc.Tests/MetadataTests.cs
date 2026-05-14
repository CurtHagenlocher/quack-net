using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Adbc;

namespace Quack.Adbc.Tests;

public class MetadataTests : IClassFixture<QuackServerFixture>
{
    private readonly QuackServerFixture _server;

    public MetadataTests(QuackServerFixture server) => _server = server;

    [Fact]
    public async Task GetTableSchema_ReturnsArrowSchemaForExistingTable()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_schema_" + Guid.NewGuid().ToString("N");

        using (AdbcStatement create = conn.CreateStatement())
        {
            create.SqlQuery =
                $"CREATE TABLE {table} (id INTEGER NOT NULL, name VARCHAR, " +
                $"price DECIMAL(10, 2), ts TIMESTAMP)";
            _ = create.ExecuteUpdate();
        }

        Schema schema = conn.GetTableSchema(catalog: null, dbSchema: null, tableName: table);

        Assert.Equal(4, schema.FieldsList.Count);
        Assert.Equal("id", schema.FieldsList[0].Name);
        Assert.IsType<Int32Type>(schema.FieldsList[0].DataType);
        Assert.Equal("name", schema.FieldsList[1].Name);
        Assert.IsType<StringType>(schema.FieldsList[1].DataType);
        Assert.Equal("price", schema.FieldsList[2].Name);
        Decimal128Type priceType = Assert.IsType<Decimal128Type>(schema.FieldsList[2].DataType);
        Assert.Equal(10, priceType.Precision);
        Assert.Equal(2, priceType.Scale);
        Assert.Equal("ts", schema.FieldsList[3].Name);
        TimestampType tsType = Assert.IsType<TimestampType>(schema.FieldsList[3].DataType);
        Assert.Equal(TimeUnit.Microsecond, tsType.Unit);

        // Suppress async-warning suppression: keep this Task async so the file's
        // other Facts can be async too without xUnit complaining about mixed shapes.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetInfo_ReturnsVendorAndDriverMetadata()
    {
        using AdbcConnection conn = OpenConnection();

        AdbcInfoCode[] requested =
        {
            AdbcInfoCode.VendorName,
            AdbcInfoCode.VendorVersion,
            AdbcInfoCode.VendorSql,
            AdbcInfoCode.VendorSubstrait,
            AdbcInfoCode.DriverName,
            AdbcInfoCode.DriverArrowVersion,
        };
        using IArrowArrayStream stream = conn.GetInfo(requested);

        // Schema: info_name uint32 not-null, info_value dense_union<...>.
        Assert.Equal("info_name", stream.Schema.FieldsList[0].Name);
        Assert.IsType<UInt32Type>(stream.Schema.FieldsList[0].DataType);
        Assert.Equal("info_value", stream.Schema.FieldsList[1].Name);
        UnionType union = Assert.IsType<UnionType>(stream.Schema.FieldsList[1].DataType);
        Assert.Equal(UnionMode.Dense, union.Mode);
        Assert.Equal(6, union.Fields.Count);
        Assert.IsType<StringType>(union.Fields[0].DataType);
        Assert.IsType<BooleanType>(union.Fields[1].DataType);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(requested.Length, batch.Length);

        UInt32Array names = Assert.IsType<UInt32Array>(batch.Column(0));
        DenseUnionArray values = Assert.IsType<DenseUnionArray>(batch.Column(1));
        StringArray strs = Assert.IsType<StringArray>(values.Fields[0]);
        BooleanArray bools = Assert.IsType<BooleanArray>(values.Fields[1]);

        // Walk the batch and reify (code -> value) by following the dense union.
        Dictionary<AdbcInfoCode, object?> got = new();
        for (int row = 0; row < batch.Length; row++)
        {
            AdbcInfoCode code = (AdbcInfoCode)names.GetValue(row)!.Value;
            byte typeId = values.TypeIds[row];
            int offset = values.ValueOffsets[row];
            got[code] = typeId switch
            {
                0 => strs.GetString(offset),
                1 => bools.GetValue(offset),
                _ => $"unexpected type_id {typeId}",
            };
        }

        Assert.Equal("DuckDB", got[AdbcInfoCode.VendorName]);
        Assert.False(string.IsNullOrEmpty((string?)got[AdbcInfoCode.VendorVersion]));
        Assert.Equal(true, got[AdbcInfoCode.VendorSql]);
        Assert.Equal(false, got[AdbcInfoCode.VendorSubstrait]);
        Assert.Equal("quack-net ADBC", got[AdbcInfoCode.DriverName]);
        Assert.False(string.IsNullOrEmpty((string?)got[AdbcInfoCode.DriverArrowVersion]));

        Assert.Null(await stream.ReadNextRecordBatchAsync());
    }

    [Fact]
    public async Task GetTableTypes_ReturnsBaseTableAndView()
    {
        using AdbcConnection conn = OpenConnection();
        using IArrowArrayStream stream = conn.GetTableTypes();

        Field only = Assert.Single(stream.Schema.FieldsList);
        Assert.Equal("table_type", only.Name);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        StringArray col = Assert.IsType<StringArray>(batch.Column(0));
        Assert.Equal(2, col.Length);
        Assert.Equal("BASE TABLE", col.GetString(0));
        Assert.Equal("VIEW", col.GetString(1));

        // Second pull returns null (end of stream).
        Assert.Null(await stream.ReadNextRecordBatchAsync());
    }

    [Fact]
    public async Task GetObjects_AllDepth_ReturnsCatalogSchemaTableColumnHierarchy()
    {
        using AdbcConnection conn = OpenConnection();
        string table = "t_objects_" + Guid.NewGuid().ToString("N");

        using (AdbcStatement create = conn.CreateStatement())
        {
            create.SqlQuery = $"CREATE TABLE main.{table} (id INTEGER, name VARCHAR)";
            _ = create.ExecuteUpdate();
        }

        using IArrowArrayStream stream = conn.GetObjects(
            depth: AdbcConnection.GetObjectsDepth.All,
            catalogPattern: null,
            dbSchemaPattern: null,
            tableNamePattern: null,
            tableTypes: null,
            columnNamePattern: null);

        Assert.Equal("catalog_name", stream.Schema.FieldsList[0].Name);
        Assert.Equal("catalog_db_schemas", stream.Schema.FieldsList[1].Name);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        StringArray catalogNames = Assert.IsType<StringArray>(batch.Column(0));
        // memory DB is named "memory" in DuckDB when opened with :memory:.
        Assert.Contains("memory", Enumerable.Range(0, catalogNames.Length).Select(i => catalogNames.GetString(i)));

        // Walk down into the nested structure looking for our test table.
        ListArray schemaList = Assert.IsType<ListArray>(batch.Column(1));
        StructArray schemas = Assert.IsType<StructArray>(schemaList.Values);
        StringArray schemaNames = Assert.IsType<StringArray>(schemas.Fields[0]);
        ListArray tableList = Assert.IsType<ListArray>(schemas.Fields[1]);
        StructArray tables = Assert.IsType<StructArray>(tableList.Values);
        StringArray tableNames = Assert.IsType<StringArray>(tables.Fields[0]);
        StringArray tableTypes = Assert.IsType<StringArray>(tables.Fields[1]);
        ListArray columnList = Assert.IsType<ListArray>(tables.Fields[2]);
        StructArray columns = Assert.IsType<StructArray>(columnList.Values);
        StringArray columnNames = Assert.IsType<StringArray>(columns.Fields[0]);
        Int32Array ordinals = Assert.IsType<Int32Array>(columns.Fields[1]);

        // Find our table. If absent, dump everything seen for a diagnostic message.
        int tableIdx = -1;
        for (int i = 0; i < tableNames.Length; i++)
        {
            if (tableNames.GetString(i) == table)
            {
                tableIdx = i;
                break;
            }
        }
        if (tableIdx == -1)
        {
            string cs = string.Join(", ", Enumerable.Range(0, catalogNames.Length).Select(i => catalogNames.GetString(i)));
            string ss = string.Join(", ", Enumerable.Range(0, schemaNames.Length).Select(i => schemaNames.GetString(i)));
            string ts = string.Join(", ", Enumerable.Range(0, tableNames.Length).Select(i => tableNames.GetString(i)));
            Assert.Fail($"Table '{table}' not found.\n  Catalogs: [{cs}]\n  Schemas: [{ss}]\n  Tables: [{ts}]");
        }
        Assert.Equal("BASE TABLE", tableTypes.GetString(tableIdx));

        // Columns of our table: id (ordinal 0), name (ordinal 1).
        int colStart = columnList.ValueOffsets[tableIdx];
        int colEnd = columnList.ValueOffsets[tableIdx + 1];
        Assert.Equal(2, colEnd - colStart);
        Assert.Equal("id", columnNames.GetString(colStart));
        Assert.Equal("name", columnNames.GetString(colStart + 1));
        // DuckDB's column_index is 1-based.
        Assert.Equal(1, ordinals.GetValue(colStart));
        Assert.Equal(2, ordinals.GetValue(colStart + 1));
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
