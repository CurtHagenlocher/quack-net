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
