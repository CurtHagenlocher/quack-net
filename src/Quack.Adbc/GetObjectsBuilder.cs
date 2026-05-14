using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Quack.Adbc;

// Builds an ADBC GetObjects IArrowArrayStream from DuckDB's system catalog.
// Returns the StandardSchemas-defined nested hierarchy
// (catalog -> schemas -> tables -> columns + constraints) — one row per
// catalog, with empty inner lists when the depth parameter stops the
// expansion short.
//
// Strategy: one SELECT joining duckdb_databases / duckdb_schemas /
// duckdb_tables U duckdb_views / duckdb_columns ordered by
// (catalog, schema, table, column_index). Walk the rows in that order and
// accumulate into nested data, then build Arrow arrays bottom-up.
internal static class GetObjectsBuilder
{
    public static IArrowArrayStream Build(
        QuackAdbcConnection connection,
        AdbcConnection.GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        string? columnNamePattern)
    {
        string sql = BuildSql(depth, catalogPattern, dbSchemaPattern, tableNamePattern, columnNamePattern);

        // Run the query through quack-net directly; bypassing the ADBC layer
        // here avoids reentrant Arrow conversion of system-catalog rows.
        QuackQueryResult result = connection.Underlying.ExecuteAsync(sql)
            .GetAwaiter().GetResult();
        IReadOnlyList<Data.DuckDbChunk> chunks = result.ToListAsync().GetAwaiter().GetResult();

        Catalog[] catalogs = WalkChunks(chunks, depth);

        Schema schema = new Schema.Builder()
            .Field(new Field("catalog_name", StringType.Default, nullable: false))
            .Field(new Field("catalog_db_schemas", new ListType(new Field("item", new StructType(SchemaStructFields), nullable: true)), nullable: true))
            .Build();

        RecordBatch batch = BuildBatch(schema, catalogs);
        return new SingleBatchArrayStream(schema, batch);
    }

    // --- SQL construction ----------------------------------------------------

    private static string BuildSql(
        AdbcConnection.GetObjectsDepth depth,
        string? catalogPattern,
        string? dbSchemaPattern,
        string? tableNamePattern,
        string? columnNamePattern)
    {
        // Use a CTE to UNION tables and views so we can carry table_type
        // forward. duckdb_columns only references tables in its base form
        // so we LEFT JOIN it to include columns of base tables. (Views'
        // columns are accessible via duckdb_columns too because the column
        // lookup just keys on table_name.)
        // GetObjectsDepth.All == 0 (deepest), Catalogs == 1, DbSchemas == 2,
        // Tables == 3. Higher numbers mean shallower output — so the natural
        // >= comparison would be backwards. Spell out the semantics here.
        bool needSchemas = depth != AdbcConnection.GetObjectsDepth.Catalogs;
        bool needTables = depth == AdbcConnection.GetObjectsDepth.All
                       || depth == AdbcConnection.GetObjectsDepth.Tables;
        bool needColumns = depth == AdbcConnection.GetObjectsDepth.All;

        System.Text.StringBuilder sb = new();
        sb.Append("WITH tv AS (\n");
        sb.Append("  SELECT database_name, schema_name, table_name, 'BASE TABLE' AS table_type FROM duckdb_tables() WHERE NOT internal\n");
        sb.Append("  UNION ALL\n");
        sb.Append("  SELECT database_name, schema_name, view_name, 'VIEW' FROM duckdb_views() WHERE NOT internal\n");
        sb.Append(")\n");
        sb.Append("SELECT d.database_name AS catalog_name");
        if (needSchemas) sb.Append(",\n  s.schema_name AS schema_name");
        if (needTables) sb.Append(",\n  tv.table_name AS table_name,\n  tv.table_type AS table_type");
        if (needColumns) sb.Append(",\n  c.column_name AS column_name,\n  CAST(c.column_index AS INTEGER) AS ordinal_position,\n  CAST(c.is_nullable AS VARCHAR) AS is_nullable");
        sb.Append("\nFROM duckdb_databases() d");
        if (needSchemas) sb.Append("\nLEFT JOIN duckdb_schemas() s ON s.database_name = d.database_name");
        if (needTables) sb.Append("\nLEFT JOIN tv ON tv.database_name = s.database_name AND tv.schema_name = s.schema_name");
        if (needColumns) sb.Append("\nLEFT JOIN duckdb_columns() c ON c.database_name = tv.database_name AND c.schema_name = tv.schema_name AND c.table_name = tv.table_name");
        sb.Append("\nWHERE NOT d.internal");
        if (catalogPattern is not null) sb.Append($"\n  AND d.database_name LIKE {Quote(catalogPattern)}");
        if (needSchemas && dbSchemaPattern is not null) sb.Append($"\n  AND (s.schema_name IS NULL OR s.schema_name LIKE {Quote(dbSchemaPattern)})");
        if (needTables && tableNamePattern is not null) sb.Append($"\n  AND (tv.table_name IS NULL OR tv.table_name LIKE {Quote(tableNamePattern)})");
        if (needColumns && columnNamePattern is not null) sb.Append($"\n  AND (c.column_name IS NULL OR c.column_name LIKE {Quote(columnNamePattern)})");
        sb.Append("\nORDER BY 1");
        if (needSchemas) sb.Append(", 2 NULLS LAST");
        if (needTables) sb.Append(", 3 NULLS LAST");
        if (needColumns) sb.Append(", 5 NULLS LAST");
        return sb.ToString();
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    // --- Row -> nested data --------------------------------------------------

    private sealed class Catalog
    {
        public required string Name { get; init; }
        public List<DbSchema> Schemas { get; } = new();
    }

    private sealed class DbSchema
    {
        public required string Name { get; init; }
        public List<TableEntry> Tables { get; } = new();
    }

    private sealed class TableEntry
    {
        public required string Name { get; init; }
        public required string Type { get; init; }
        public List<ColumnEntry> Columns { get; } = new();
    }

    private sealed class ColumnEntry
    {
        public required string Name { get; init; }
        public required int OrdinalPosition { get; init; }
        public required string? IsNullable { get; init; }
    }

    private static Catalog[] WalkChunks(IReadOnlyList<Data.DuckDbChunk> chunks, AdbcConnection.GetObjectsDepth depth)
    {
        bool hasSchemas = depth != AdbcConnection.GetObjectsDepth.Catalogs;
        bool hasTables = depth == AdbcConnection.GetObjectsDepth.All
                      || depth == AdbcConnection.GetObjectsDepth.Tables;
        bool hasColumns = depth == AdbcConnection.GetObjectsDepth.All;

        Dictionary<string, Catalog> catalogs = new(StringComparer.Ordinal);
        Dictionary<(string c, string s), DbSchema> schemas = new();
        Dictionary<(string c, string s, string t), TableEntry> tables = new();

        foreach (Data.DuckDbChunk chunk in chunks)
        {
            // The catalog_name column is at index 0 and is always present.
            Data.VarBytesColumn catalogCol = (Data.VarBytesColumn)chunk.Columns[0];
            Data.VarBytesColumn? schemaCol = hasSchemas ? (Data.VarBytesColumn)chunk.Columns[1] : null;
            Data.VarBytesColumn? tableCol = hasTables ? (Data.VarBytesColumn)chunk.Columns[2] : null;
            Data.VarBytesColumn? tableTypeCol = hasTables ? (Data.VarBytesColumn)chunk.Columns[3] : null;
            Data.VarBytesColumn? columnNameCol = hasColumns ? (Data.VarBytesColumn)chunk.Columns[4] : null;
            Data.FixedSizeColumn? ordinalCol = hasColumns ? (Data.FixedSizeColumn)chunk.Columns[5] : null;
            Data.VarBytesColumn? isNullableCol = hasColumns ? (Data.VarBytesColumn)chunk.Columns[6] : null;

            for (int i = 0; i < chunk.RowCount; i++)
            {
                string catalogName = ReadString(catalogCol, i)!;
                if (!catalogs.TryGetValue(catalogName, out Catalog? cat))
                {
                    cat = new Catalog { Name = catalogName };
                    catalogs[catalogName] = cat;
                }
                if (!hasSchemas) continue;
                string? schemaName = ReadString(schemaCol!, i);
                if (schemaName is null) continue;
                if (!schemas.TryGetValue((catalogName, schemaName), out DbSchema? sch))
                {
                    sch = new DbSchema { Name = schemaName };
                    cat.Schemas.Add(sch);
                    schemas[(catalogName, schemaName)] = sch;
                }
                if (!hasTables) continue;
                string? tableName = ReadString(tableCol!, i);
                if (tableName is null) continue;
                if (!tables.TryGetValue((catalogName, schemaName, tableName), out TableEntry? tab))
                {
                    tab = new TableEntry { Name = tableName, Type = ReadString(tableTypeCol!, i) ?? "BASE TABLE" };
                    sch.Tables.Add(tab);
                    tables[(catalogName, schemaName, tableName)] = tab;
                }
                if (!hasColumns) continue;
                string? columnName = ReadString(columnNameCol!, i);
                if (columnName is null) continue;
                int ordinal = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(ordinalCol!.GetBytes(i));
                tab.Columns.Add(new ColumnEntry
                {
                    Name = columnName,
                    OrdinalPosition = ordinal,
                    IsNullable = ReadString(isNullableCol!, i),
                });
            }
        }
        return catalogs.Values.ToArray();
    }

    private static string? ReadString(Data.VarBytesColumn col, int rowIndex)
    {
        if (col.IsNull(rowIndex)) return null;
        ReadOnlyMemory<byte>? slot = col.Values[rowIndex];
        return slot.HasValue ? System.Text.Encoding.UTF8.GetString(slot.Value.Span) : null;
    }

    // --- Arrow array construction --------------------------------------------

    // Field lists pulled from Apache.Arrow.Adbc.StandardSchemas — mirrored here
    // so we can build StructTypes without a runtime dependency on the
    // standard schemas object. Declaration order matters: each list must come
    // before any list that references it (static-field initializers run
    // top-down).
    private static readonly IReadOnlyList<Field> ColumnStructFields =
    [
        new Field("column_name", StringType.Default, nullable: false),
        new Field("ordinal_position", Int32Type.Default, nullable: true),
        new Field("remarks", StringType.Default, nullable: true),
        new Field("xdbc_data_type", Int16Type.Default, nullable: true),
        new Field("xdbc_type_name", StringType.Default, nullable: true),
        new Field("xdbc_column_size", Int32Type.Default, nullable: true),
        new Field("xdbc_decimal_digits", Int16Type.Default, nullable: true),
        new Field("xdbc_num_prec_radix", Int16Type.Default, nullable: true),
        new Field("xdbc_nullable", Int16Type.Default, nullable: true),
        new Field("xdbc_column_def", StringType.Default, nullable: true),
        new Field("xdbc_sql_data_type", Int16Type.Default, nullable: true),
        new Field("xdbc_datetime_sub", Int16Type.Default, nullable: true),
        new Field("xdbc_char_octet_length", Int32Type.Default, nullable: true),
        new Field("xdbc_is_nullable", StringType.Default, nullable: true),
        new Field("xdbc_scope_catalog", StringType.Default, nullable: true),
        new Field("xdbc_scope_schema", StringType.Default, nullable: true),
        new Field("xdbc_scope_table", StringType.Default, nullable: true),
        new Field("xdbc_is_autoincrement", BooleanType.Default, nullable: true),
        new Field("xdbc_is_generatedcolumn", BooleanType.Default, nullable: true),
    ];

    private static readonly IReadOnlyList<Field> ConstraintStructFields =
    [
        new Field("constraint_name", StringType.Default, nullable: false),
        new Field("constraint_type", StringType.Default, nullable: false),
        new Field("constraint_column_names", new ListType(new Field("item", StringType.Default, nullable: true)), nullable: false),
        new Field("constraint_column_usage", new ListType(new Field("item",
            new StructType(
            [
                new Field("fk_catalog", StringType.Default, nullable: true),
                new Field("fk_db_schema", StringType.Default, nullable: true),
                new Field("fk_table", StringType.Default, nullable: false),
                new Field("fk_column_name", StringType.Default, nullable: false),
            ]), nullable: true)), nullable: false),
    ];

    private static readonly IReadOnlyList<Field> TableStructFields =
    [
        new Field("table_name", StringType.Default, nullable: false),
        new Field("table_type", StringType.Default, nullable: false),
        new Field("table_columns", new ListType(new Field("item", new StructType(ColumnStructFields), nullable: true)), nullable: true),
        new Field("table_constraints", new ListType(new Field("item", new StructType(ConstraintStructFields), nullable: true)), nullable: true),
    ];

    private static readonly IReadOnlyList<Field> SchemaStructFields =
    [
        new Field("db_schema_name", StringType.Default, nullable: true),
        new Field("db_schema_tables", new ListType(new Field("item", new StructType(TableStructFields), nullable: true)), nullable: true),
    ];

    private static RecordBatch BuildBatch(Schema schema, Catalog[] catalogs)
    {
        StringArray.Builder catNames = new();
        ArrowBuffer.Builder<int> schemaListOffsets = new();
        schemaListOffsets.Append(0);
        List<DbSchema> allSchemas = new();
        int schemaCursor = 0;
        foreach (Catalog c in catalogs)
        {
            catNames.Append(c.Name);
            allSchemas.AddRange(c.Schemas);
            schemaCursor += c.Schemas.Count;
            schemaListOffsets.Append(schemaCursor);
        }

        StructArray schemasArray = BuildSchemasArray(allSchemas);
        ListType schemaListType = new(new Field("item", new StructType(SchemaStructFields), nullable: true));
        ListArray schemaList = new(schemaListType, length: catalogs.Length,
            valueOffsetsBuffer: schemaListOffsets.Build(),
            values: schemasArray,
            nullBitmapBuffer: ArrowBuffer.Empty,
            nullCount: 0, offset: 0);

        return new RecordBatch(schema, [catNames.Build(), schemaList], length: catalogs.Length);
    }

    private static StructArray BuildSchemasArray(List<DbSchema> schemas)
    {
        StringArray.Builder names = new();
        ArrowBuffer.Builder<int> tableListOffsets = new();
        tableListOffsets.Append(0);
        List<TableEntry> allTables = new();
        int cursor = 0;
        foreach (DbSchema s in schemas)
        {
            names.Append(s.Name);
            allTables.AddRange(s.Tables);
            cursor += s.Tables.Count;
            tableListOffsets.Append(cursor);
        }

        StructArray tablesArray = BuildTablesArray(allTables);
        ListType tableListType = new(new Field("item", new StructType(TableStructFields), nullable: true));
        ListArray tableList = new(tableListType, length: schemas.Count,
            valueOffsetsBuffer: tableListOffsets.Build(),
            values: tablesArray,
            nullBitmapBuffer: ArrowBuffer.Empty,
            nullCount: 0, offset: 0);

        StructType st = new(SchemaStructFields);
        return new StructArray(st, schemas.Count, [names.Build(), tableList], ArrowBuffer.Empty, nullCount: 0);
    }

    private static StructArray BuildTablesArray(List<TableEntry> tables)
    {
        StringArray.Builder names = new();
        StringArray.Builder types = new();
        ArrowBuffer.Builder<int> columnListOffsets = new();
        columnListOffsets.Append(0);
        ArrowBuffer.Builder<int> constraintListOffsets = new();
        constraintListOffsets.Append(0);
        List<ColumnEntry> allColumns = new();
        int cursor = 0;
        foreach (TableEntry t in tables)
        {
            names.Append(t.Name);
            types.Append(t.Type);
            allColumns.AddRange(t.Columns);
            cursor += t.Columns.Count;
            columnListOffsets.Append(cursor);
            // table_constraints is required (non-nullable list), but we emit
            // an empty list per table for now.
            constraintListOffsets.Append(0);
        }

        StructArray columnsArray = BuildColumnsArray(allColumns);
        ListType columnListType = new(new Field("item", new StructType(ColumnStructFields), nullable: true));
        ListArray columnList = new(columnListType, length: tables.Count,
            valueOffsetsBuffer: columnListOffsets.Build(),
            values: columnsArray,
            nullBitmapBuffer: ArrowBuffer.Empty,
            nullCount: 0, offset: 0);

        // Empty constraints list — same offsets buffer (all zero) but a
        // length-0 child struct array.
        StructArray emptyConstraints = new(
            new StructType(ConstraintStructFields), 0,
            BuildEmptyConstraintChildren(),
            ArrowBuffer.Empty, nullCount: 0);
        ListType constraintListType = new(new Field("item", new StructType(ConstraintStructFields), nullable: true));
        ListArray constraintList = new(constraintListType, length: tables.Count,
            valueOffsetsBuffer: constraintListOffsets.Build(),
            values: emptyConstraints,
            nullBitmapBuffer: ArrowBuffer.Empty,
            nullCount: 0, offset: 0);

        StructType st = new(TableStructFields);
        return new StructArray(st, tables.Count,
            [names.Build(), types.Build(), columnList, constraintList],
            ArrowBuffer.Empty, nullCount: 0);
    }

    private static IArrowArray[] BuildEmptyConstraintChildren()
    {
        // Constraints are emitted as empty for v1 — we still need to provide
        // empty child arrays of the right types so the StructType validates.
        ListType nameListType = new(new Field("item", StringType.Default, nullable: true));
        ListType usageListType = new(new Field("item", new StructType(
        [
            new Field("fk_catalog", StringType.Default, nullable: true),
            new Field("fk_db_schema", StringType.Default, nullable: true),
            new Field("fk_table", StringType.Default, nullable: false),
            new Field("fk_column_name", StringType.Default, nullable: false),
        ]), nullable: true));
        StructArray emptyUsageEntries = new(
            new StructType(
            [
                new Field("fk_catalog", StringType.Default, nullable: true),
                new Field("fk_db_schema", StringType.Default, nullable: true),
                new Field("fk_table", StringType.Default, nullable: false),
                new Field("fk_column_name", StringType.Default, nullable: false),
            ]), 0,
            [
                new StringArray.Builder().Build(),
                new StringArray.Builder().Build(),
                new StringArray.Builder().Build(),
                new StringArray.Builder().Build(),
            ],
            ArrowBuffer.Empty, nullCount: 0);

        return
        [
            new StringArray.Builder().Build(),
            new StringArray.Builder().Build(),
            new ListArray(nameListType, length: 0,
                valueOffsetsBuffer: BuildSingleZeroOffsets(),
                values: new StringArray.Builder().Build(),
                nullBitmapBuffer: ArrowBuffer.Empty, nullCount: 0, offset: 0),
            new ListArray(usageListType, length: 0,
                valueOffsetsBuffer: BuildSingleZeroOffsets(),
                values: emptyUsageEntries,
                nullBitmapBuffer: ArrowBuffer.Empty, nullCount: 0, offset: 0),
        ];
    }

    private static ArrowBuffer BuildSingleZeroOffsets()
    {
        ArrowBuffer.Builder<int> b = new();
        b.Append(0);
        return b.Build();
    }

    private static StructArray BuildColumnsArray(List<ColumnEntry> columns)
    {
        StringArray.Builder colNames = new();
        Int32Array.Builder ordinals = new();
        foreach (ColumnEntry c in columns)
        {
            colNames.Append(c.Name);
            ordinals.Append(c.OrdinalPosition);
        }

        // Build empty-of-correct-type child arrays for the 17 xdbc_* fields.
        // We don't populate them in this MVP but the StructArray demands
        // arrays of the right length and type for every field.
        IArrowArray[] children = new IArrowArray[ColumnStructFields.Count];
        children[0] = colNames.Build();
        children[1] = ordinals.Build();
        for (int i = 2; i < ColumnStructFields.Count; i++)
        {
            children[i] = BuildEmptyValuesArray(ColumnStructFields[i].DataType, columns.Count);
        }

        StructType st = new(ColumnStructFields);
        return new StructArray(st, columns.Count, children, ArrowBuffer.Empty, nullCount: 0);
    }

    private static IArrowArray BuildEmptyValuesArray(IArrowType type, int length)
    {
        // All values null — Arrow uses an empty (or absent) value buffer plus
        // a validity bitmap of all-zeros. ArrayBuffer.Empty for value buffers
        // works for primitives because the data is null per row.
        int nullBytes = (length + 7) / 8;
        byte[] validity = new byte[nullBytes]; // all zero -> all NULL
        ArrowBuffer validityBuf = length == 0 ? ArrowBuffer.Empty : new ArrowBuffer(validity);

        return type switch
        {
            StringType => new StringArray(
                valueOffsetsBuffer: BuildAllZeroOffsets(length),
                dataBuffer: ArrowBuffer.Empty,
                nullBitmapBuffer: validityBuf,
                length: length, nullCount: length, offset: 0),
            Int16Type => new Int16Array(new ArrowBuffer(new byte[length * 2]), validityBuf, length, length, 0),
            Int32Type => new Int32Array(new ArrowBuffer(new byte[length * 4]), validityBuf, length, length, 0),
            BooleanType => new BooleanArray(new ArrowBuffer(new byte[(length + 7) / 8]), validityBuf, length, length, 0),
            _ => throw new NotSupportedException($"Empty-values array build for '{type.GetType().Name}' is not supported."),
        };
    }

    private static ArrowBuffer BuildAllZeroOffsets(int length)
    {
        ArrowBuffer.Builder<int> b = new(length + 1);
        for (int i = 0; i <= length; i++) b.Append(0);
        return b.Build();
    }

    private sealed class SingleBatchArrayStream : IArrowArrayStream
    {
        private readonly Schema _schema;
        private RecordBatch? _batch;

        public SingleBatchArrayStream(Schema schema, RecordBatch batch)
        {
            _schema = schema;
            _batch = batch;
        }

        public Schema Schema => _schema;

        public ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
        {
            RecordBatch? next = _batch;
            _batch = null;
            return ValueTask.FromResult(next);
        }

        public void Dispose()
        {
            _batch?.Dispose();
            _batch = null;
        }
    }
}
