// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Types;

// Mirror of duckdb::LogicalTypeInfoType (ExtraTypeInfoType before DuckDB 2.0).
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
    // LEGACY_AGGREGATE_STATE_TYPE_INFO since DuckDB 2.0.
    AggregateState = 8,
    Array = 9,
    Any = 10,
    IntegerLiteral = 11,
    Template = 12,
    Geo = 13,
}
