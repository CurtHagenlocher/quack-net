namespace Quack.Data;

// Mirror of duckdb::VectorType
// (src/include/duckdb/common/enums/vector_type.hpp in duckdb/duckdb v1.5.x).
// The default-on-the-wire value is FLAT.
internal enum VectorType : byte
{
    Flat = 0,
    Fsst = 1,
    Constant = 2,
    Dictionary = 3,
    Sequence = 4,
}
