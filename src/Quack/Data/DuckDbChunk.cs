using Quack.Types;

namespace Quack.Data;

// A materialized DuckDB DataChunk: a row count, a vector of column LogicalTypes,
// and a parallel vector of columnar payloads. Mirrors duckdb::DataChunk's
// shape minus the in-memory caching machinery.
public sealed class DuckDbChunk
{
    public required IReadOnlyList<LogicalType> Types { get; init; }
    public required IReadOnlyList<DuckDbColumn> Columns { get; init; }
    public int RowCount { get; init; }

    public int ColumnCount => Columns.Count;
}
