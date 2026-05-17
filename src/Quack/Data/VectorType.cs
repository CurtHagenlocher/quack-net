// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
