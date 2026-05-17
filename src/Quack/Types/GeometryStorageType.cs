// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Types;

// Mirror of duckdb::GeometryStorageType (src/include/duckdb/common/types/geometry.hpp).
// Tags the bytes inside a GEOMETRY column on the wire (field 99) so the
// server knows whether to parse them as the legacy double-aligned SPATIAL
// representation or as plain WKB.
public enum GeometryStorageType : byte
{
    Spatial = 0,
    Wkb = 1,
}
