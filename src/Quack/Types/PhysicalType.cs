namespace Quack.Types;

// Mirror of duckdb::PhysicalType
// (src/include/duckdb/common/types.hpp in duckdb/duckdb v1.5.x).
public enum PhysicalType : byte
{
    Bool = 1,
    UInt8 = 2,
    Int8 = 3,
    UInt16 = 4,
    Int16 = 5,
    UInt32 = 6,
    Int32 = 7,
    UInt64 = 8,
    Int64 = 9,
    Float = 11,
    Double = 12,
    Interval = 21,
    List = 23,
    Struct = 24,
    Array = 29,
    Varchar = 200,
    UInt128 = 203,
    Int128 = 204,
    Unknown = 205,
    Bit = 206,
    Invalid = 255,
}
