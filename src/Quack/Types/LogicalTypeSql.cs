// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Quack.Types;

// Renders a LogicalType as DuckDB SQL type text (what LogicalType::ToString
// gives on the server), e.g. for the NULL::STRUCT(...) prototype the data
// stream scan takes.
internal static class LogicalTypeSql
{
    public static string ToSql(LogicalType type)
    {
        StringBuilder sb = new();
        Append(sb, type);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, LogicalType type)
    {
        switch (type.Id)
        {
            case LogicalTypeId.Boolean: sb.Append("BOOLEAN"); return;
            case LogicalTypeId.TinyInt: sb.Append("TINYINT"); return;
            case LogicalTypeId.SmallInt: sb.Append("SMALLINT"); return;
            case LogicalTypeId.Integer: sb.Append("INTEGER"); return;
            case LogicalTypeId.BigInt: sb.Append("BIGINT"); return;
            case LogicalTypeId.HugeInt: sb.Append("HUGEINT"); return;
            case LogicalTypeId.UTinyInt: sb.Append("UTINYINT"); return;
            case LogicalTypeId.USmallInt: sb.Append("USMALLINT"); return;
            case LogicalTypeId.UInteger: sb.Append("UINTEGER"); return;
            case LogicalTypeId.UBigInt: sb.Append("UBIGINT"); return;
            case LogicalTypeId.UHugeInt: sb.Append("UHUGEINT"); return;
            case LogicalTypeId.Float: sb.Append("FLOAT"); return;
            case LogicalTypeId.Double: sb.Append("DOUBLE"); return;
            case LogicalTypeId.Date: sb.Append("DATE"); return;
            case LogicalTypeId.Time: sb.Append("TIME"); return;
            case LogicalTypeId.TimeNs: sb.Append("TIME_NS"); return;
            case LogicalTypeId.TimeTz: sb.Append("TIME WITH TIME ZONE"); return;
            case LogicalTypeId.Timestamp: sb.Append("TIMESTAMP"); return;
            case LogicalTypeId.TimestampSec: sb.Append("TIMESTAMP_S"); return;
            case LogicalTypeId.TimestampMs: sb.Append("TIMESTAMP_MS"); return;
            case LogicalTypeId.TimestampNs: sb.Append("TIMESTAMP_NS"); return;
            case LogicalTypeId.TimestampTz: sb.Append("TIMESTAMP WITH TIME ZONE"); return;
            case LogicalTypeId.TimestampTzNs: sb.Append("TIMESTAMPTZ_NS"); return;
            case LogicalTypeId.Interval: sb.Append("INTERVAL"); return;
            case LogicalTypeId.Uuid: sb.Append("UUID"); return;
            // DuckDB 2.0 stores a column of type NULL; the name is a keyword, so quoted.
            case LogicalTypeId.SqlNull: sb.Append("\"NULL\""); return;
            case LogicalTypeId.Varchar:
            case LogicalTypeId.Char:
            case LogicalTypeId.StringLiteral:
                sb.Append("VARCHAR");
                return;
            case LogicalTypeId.Blob: sb.Append("BLOB"); return;
            case LogicalTypeId.Bit: sb.Append("BIT"); return;
            case LogicalTypeId.BigNum: sb.Append("BIGNUM"); return;
            case LogicalTypeId.Geometry: sb.Append("GEOMETRY"); return;
            case LogicalTypeId.Variant: sb.Append("VARIANT"); return;
            case LogicalTypeId.Decimal when type.TypeInfo is DecimalTypeInfo dec:
                sb.Append("DECIMAL(").Append(dec.Width).Append(", ").Append(dec.Scale).Append(')');
                return;
            case LogicalTypeId.Enum when type.TypeInfo is EnumTypeInfo en:
                sb.Append("ENUM(");
                for (int i = 0; i < en.Values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('\'').Append(en.Values[i].Replace("'", "''", StringComparison.Ordinal)).Append('\'');
                }
                sb.Append(')');
                return;
            case LogicalTypeId.List when type.TypeInfo is ListTypeInfo list:
                Append(sb, list.ChildType);
                sb.Append("[]");
                return;
            case LogicalTypeId.Array when type.TypeInfo is ArrayTypeInfo array:
                Append(sb, array.ChildType);
                sb.Append('[').Append(array.Size).Append(']');
                return;
            case LogicalTypeId.Map when type.TypeInfo is ListTypeInfo map
                                        && map.ChildType.TypeInfo is StructTypeInfo entry
                                        && entry.ChildTypes.Count == 2:
                sb.Append("MAP(");
                Append(sb, entry.ChildTypes[0].Value);
                sb.Append(", ");
                Append(sb, entry.ChildTypes[1].Value);
                sb.Append(')');
                return;
            case LogicalTypeId.Struct when type.TypeInfo is StructTypeInfo st:
                if (st.ChildTypes.Count > 0 && st.ChildTypes.All(c => c.Key.Length == 0))
                {
                    // DuckDB 2.0 reads an all-unnamed STRUCT as TUPLE.
                    AppendMembers(sb, "TUPLE(", st.ChildTypes, skip: 0, named: false);
                    return;
                }
                if (st.ChildTypes.Any(c => c.Key.Length == 0))
                {
                    throw new NotSupportedException(
                        $"STRUCT '{type}' mixes named and unnamed fields, which DuckDB cannot declare.");
                }
                AppendMembers(sb, "STRUCT(", st.ChildTypes, skip: 0, named: true);
                return;
            case LogicalTypeId.Tuple when type.TypeInfo is StructTypeInfo tuple:
                AppendMembers(sb, "TUPLE(", tuple.ChildTypes, skip: 0, named: false);
                return;
            case LogicalTypeId.Union when type.TypeInfo is StructTypeInfo union:
                // Child 0 is the tag; the members follow.
                AppendMembers(sb, "UNION(", union.ChildTypes, skip: 1, named: true);
                return;
            default:
                throw new NotSupportedException($"DuckDB type '{type}' has no SQL rendering in this client.");
        }
    }

    private static void AppendMembers(
        StringBuilder sb, string open, IReadOnlyList<KeyValuePair<string, LogicalType>> members, int skip, bool named)
    {
        sb.Append(open);
        for (int i = skip; i < members.Count; i++)
        {
            if (i > skip) sb.Append(", ");
            if (named)
            {
                sb.Append(QuoteIdentifier(members[i].Key)).Append(' ');
            }
            Append(sb, members[i].Value);
        }
        sb.Append(')');
    }

    public static string QuoteIdentifier(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
