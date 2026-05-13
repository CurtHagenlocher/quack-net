namespace Quack.Types;

// Mirror of duckdb::ExtraTypeInfoType.
internal enum ExtraTypeInfoKind : byte
{
    Invalid = 0,
    Generic = 1,
    Decimal = 2,
    String = 3,
    List = 4,
    Struct = 5,
    Enum = 6,
    Unbound = 7,
    AggregateState = 8,
    Array = 9,
    Any = 10,
    IntegerLiteral = 11,
    Template = 12,
    Geo = 13,
}
