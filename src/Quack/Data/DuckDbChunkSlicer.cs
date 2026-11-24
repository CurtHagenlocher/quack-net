// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Data;

// Cuts a chunk into pieces of at most StandardVectorSize rows. A chunk sent
// to the server goes into DuckDB's pipeline as it is (the client-data scan
// references it), so it must not exceed DuckDB's vector size.
internal static class DuckDbChunkSlicer
{
    // duckdb::STANDARD_VECTOR_SIZE in the default build.
    public const int StandardVectorSize = 2048;

    public static IEnumerable<DuckDbChunk> Split(DuckDbChunk chunk, int maxRows = StandardVectorSize)
    {
        if (chunk.RowCount <= maxRows)
        {
            yield return chunk;
            yield break;
        }
        for (int start = 0; start < chunk.RowCount; start += maxRows)
        {
            int count = Math.Min(maxRows, chunk.RowCount - start);
            yield return new DuckDbChunk
            {
                Types = chunk.Types,
                Columns = chunk.Columns.Select(c => Slice(c, start, count)).ToArray(),
                RowCount = count,
            };
        }
    }

    internal static DuckDbColumn Slice(DuckDbColumn column, int start, int count)
    {
        ValidityMask validity = SliceValidity(column.Validity, start, count);
        switch (column)
        {
            case FixedSizeColumn f:
                return new FixedSizeColumn
                {
                    Type = f.Type,
                    Count = count,
                    Validity = validity,
                    ElementSize = f.ElementSize,
                    Data = f.Data.Slice(start * f.ElementSize, count * f.ElementSize),
                };
            case VarBytesColumn v:
                return new VarBytesColumn
                {
                    Type = v.Type,
                    Count = count,
                    Validity = validity,
                    Values = v.Values.AsSpan(start, count).ToArray(),
                };
            case StructColumn s:
                return new StructColumn
                {
                    Type = s.Type,
                    Count = count,
                    Validity = validity,
                    Fields = s.Fields.Select(f => Slice(f, start, count)).ToArray(),
                };
            case ArrayColumn a:
                return new ArrayColumn
                {
                    Type = a.Type,
                    Count = count,
                    Validity = validity,
                    ArraySize = a.ArraySize,
                    Child = Slice(a.Child, checked(start * (int)a.ArraySize), checked(count * (int)a.ArraySize)),
                };
            case ListColumn l:
                return SliceList(l, start, count, validity);
            default:
                throw new NotSupportedException($"Cannot slice column kind '{column.GetType().Name}'.");
        }
    }

    // Keeps the child span the selected rows reference and rebases their offsets onto it.
    private static ListColumn SliceList(ListColumn list, int start, int count, ValidityMask validity)
    {
        ulong childStart = ulong.MaxValue;
        ulong childEnd = 0;
        for (int i = start; i < start + count; i++)
        {
            (ulong offset, ulong length) = list.Entries[i];
            if (list.Validity.IsValid(i) && length > 0)
            {
                childStart = Math.Min(childStart, offset);
                childEnd = Math.Max(childEnd, offset + length);
            }
        }
        if (childStart == ulong.MaxValue)
        {
            childStart = 0;
            childEnd = 0;
        }

        (ulong, ulong)[] entries = new (ulong, ulong)[count];
        for (int i = 0; i < count; i++)
        {
            (ulong offset, ulong length) = list.Entries[start + i];
            entries[i] = list.Validity.IsValid(start + i) && length > 0 ? (offset - childStart, length) : (0, 0);
        }
        return new ListColumn
        {
            Type = list.Type,
            Count = count,
            Validity = validity,
            Entries = entries,
            Child = Slice(list.Child, checked((int)childStart), checked((int)(childEnd - childStart))),
        };
    }

    private static ValidityMask SliceValidity(ValidityMask validity, int start, int count)
    {
        if (validity.IsAllValid)
        {
            return ValidityMask.AllValid;
        }
        byte[] bytes = new byte[ValidityMask.RequiredByteCount(count)];
        for (int i = 0; i < count; i++)
        {
            if (validity.IsValid(start + i))
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
        return new ValidityMask(bytes);
    }
}
