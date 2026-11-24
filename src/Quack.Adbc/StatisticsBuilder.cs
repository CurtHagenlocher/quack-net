// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Quack.Data;

namespace Quack.Adbc;

// ADBC 1.1 GetStatistics / GetStatisticNames, with the result schemas of adbc.h:
//   catalog_name utf8, catalog_db_schemas list<struct<
//     db_schema_name utf8, db_schema_statistics list<struct<
//       table_name utf8 not null, column_name utf8, statistic_key int16 not null,
//       statistic_value dense_union<int64, uint64, float64, binary> not null,
//       statistic_is_approximate bool not null>> not null>> not null
// DuckDB keeps no per-column statistics it can answer cheaply, so the result
// is one row count per table: ADBC_STATISTIC_ROW_COUNT_KEY from
// duckdb_tables().estimated_size, approximate.
internal static class StatisticsBuilder
{
    // ADBC_STATISTIC_ROW_COUNT_KEY.
    internal const short RowCountKey = 6;

    private static readonly UnionType ValueType = new(
        [
            new Field("int64", Int64Type.Default, nullable: true),
            new Field("uint64", UInt64Type.Default, nullable: true),
            new Field("float64", DoubleType.Default, nullable: true),
            new Field("binary", BinaryType.Default, nullable: true),
        ],
        [0, 1, 2, 3],
        UnionMode.Dense);

    private static readonly StructType StatisticType = new(
    [
        new Field("table_name", StringType.Default, nullable: false),
        new Field("column_name", StringType.Default, nullable: true),
        new Field("statistic_key", Int16Type.Default, nullable: false),
        new Field("statistic_value", ValueType, nullable: false),
        new Field("statistic_is_approximate", BooleanType.Default, nullable: false),
    ]);

    private static readonly StructType DbSchemaType = new(
    [
        new Field("db_schema_name", StringType.Default, nullable: true),
        new Field("db_schema_statistics", new ListType(new Field("item", StatisticType, nullable: true)), nullable: false),
    ]);

    internal static readonly Schema StatisticsSchema = new Schema.Builder()
        .Field(new Field("catalog_name", StringType.Default, nullable: true))
        .Field(new Field("catalog_db_schemas", new ListType(new Field("item", DbSchemaType, nullable: true)), nullable: false))
        .Build();

    internal static readonly Schema StatisticNamesSchema = new Schema.Builder()
        .Field(new Field("statistic_name", StringType.Default, nullable: false))
        .Field(new Field("statistic_key", Int16Type.Default, nullable: false))
        .Build();

    // No driver-specific statistics.
    public static IArrowArrayStream BuildNames()
        => new OneBatchStream(StatisticNamesSchema, new RecordBatch(StatisticNamesSchema,
            [new StringArray.Builder().Build(), new Int16Array.Builder().Build()], 0));

    public static IArrowArrayStream Build(QuackAdbcConnection connection, string? catalogPattern, string? dbSchemaPattern, string? tableNamePattern)
    {
        StringBuilder sql = new("SELECT database_name, schema_name, table_name, estimated_size FROM duckdb_tables() WHERE estimated_size IS NOT NULL");
        if (catalogPattern is not null) sql.Append(" AND database_name LIKE ").Append(Quote(catalogPattern));
        if (dbSchemaPattern is not null) sql.Append(" AND schema_name LIKE ").Append(Quote(dbSchemaPattern));
        if (tableNamePattern is not null) sql.Append(" AND table_name LIKE ").Append(Quote(tableNamePattern));
        sql.Append(" ORDER BY database_name, schema_name, table_name");
        IReadOnlyList<DuckDbChunk> chunks = connection.RunQuery(sql.ToString(), out _, "GetStatistics");

        // Rows sorted by catalog, schema: group as they come.
        StringArray.Builder catalogNames = new();
        ArrowBuffer.Builder<int> catalogOffsets = new();
        StringArray.Builder schemaNames = new();
        ArrowBuffer.Builder<int> schemaOffsets = new();
        StringArray.Builder tableNames = new();
        Int64Array.Builder rowCounts = new();
        catalogOffsets.Append(0);
        schemaOffsets.Append(0);
        int catalogCount = 0, schemaCount = 0, statisticCount = 0;
        string? currentCatalog = null, currentSchema = null;
        foreach (DuckDbChunk chunk in chunks)
        {
            for (int row = 0; row < chunk.RowCount; row++)
            {
                string catalog = Text(chunk, 0, row);
                string schema = Text(chunk, 1, row);
                if (catalog != currentCatalog)
                {
                    if (currentCatalog is not null)
                    {
                        schemaOffsets.Append(statisticCount);
                        catalogOffsets.Append(++schemaCount);
                    }
                    catalogNames.Append(catalog);
                    catalogCount++;
                    schemaNames.Append(schema);
                    currentCatalog = catalog;
                    currentSchema = schema;
                }
                else if (schema != currentSchema)
                {
                    schemaOffsets.Append(statisticCount);
                    schemaCount++;
                    schemaNames.Append(schema);
                    currentSchema = schema;
                }
                tableNames.Append(Text(chunk, 2, row));
                rowCounts.Append(BinaryPrimitives.ReadInt64LittleEndian(((FixedSizeColumn)chunk.Columns[3]).GetBytes(row)));
                statisticCount++;
            }
        }
        if (currentCatalog is not null)
        {
            schemaOffsets.Append(statisticCount);
            catalogOffsets.Append(++schemaCount);
        }

        StructArray statistics = new(StatisticType, statisticCount,
        [
            tableNames.Build(),
            Nulls(statisticCount),
            Repeat(RowCountKey, statisticCount),
            RowCountValues(rowCounts.Build()),
            Trues(statisticCount),
        ], ArrowBuffer.Empty, 0);
        StructArray schemas = new(DbSchemaType, schemaCount,
        [
            schemaNames.Build(),
            new ListArray(DbSchemaType.Fields[1].DataType, schemaCount, schemaOffsets.Build(), statistics, ArrowBuffer.Empty, 0),
        ], ArrowBuffer.Empty, 0);
        ListArray catalogSchemas = new(StatisticsSchema.FieldsList[1].DataType, catalogCount, catalogOffsets.Build(), schemas, ArrowBuffer.Empty, 0);
        RecordBatch batch = new(StatisticsSchema, [catalogNames.Build(), catalogSchemas], catalogCount);
        return new OneBatchStream(StatisticsSchema, batch);
    }

    private static DenseUnionArray RowCountValues(Int64Array counts)
    {
        int length = counts.Length;
        ArrowBuffer.Builder<byte> typeIds = new(length);
        ArrowBuffer.Builder<int> offsets = new(length);
        for (int i = 0; i < length; i++)
        {
            typeIds.Append(0);
            offsets.Append(i);
        }
        return new DenseUnionArray(ValueType, length,
            [counts, new UInt64Array.Builder().Build(), new DoubleArray.Builder().Build(), new BinaryArray.Builder().Build()],
            typeIds.Build(), offsets.Build(), 0);
    }

    private static StringArray Nulls(int length)
    {
        StringArray.Builder builder = new();
        for (int i = 0; i < length; i++) builder.AppendNull();
        return builder.Build();
    }

    private static Int16Array Repeat(short value, int length)
    {
        Int16Array.Builder builder = new();
        for (int i = 0; i < length; i++) builder.Append(value);
        return builder.Build();
    }

    private static BooleanArray Trues(int length)
    {
        BooleanArray.Builder builder = new();
        for (int i = 0; i < length; i++) builder.Append(true);
        return builder.Build();
    }

    private static string Text(DuckDbChunk chunk, int column, int row)
        => Encoding.UTF8.GetString((((VarBytesColumn)chunk.Columns[column]).Values[row] ?? ReadOnlyMemory<byte>.Empty).Span);

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed class OneBatchStream(Schema schema, RecordBatch batch) : IArrowArrayStream
    {
        private RecordBatch? _batch = batch;

        public Schema Schema { get; } = schema;

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
