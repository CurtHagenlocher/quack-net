// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Quack.Data;

// Builds a column from selected rows of another, for every column kind
// (constant and dictionary vectors of nested types, VARIANT included).
internal static class ColumnGather
{
    public static DuckDbColumn Take(DuckDbColumn column, ReadOnlySpan<int> rows)
    {
        int count = rows.Length;
        ValidityMask validity = TakeValidity(column.Validity, rows);
        switch (column)
        {
            case FixedSizeColumn f:
            {
                byte[] data = new byte[f.ElementSize * count];
                for (int i = 0; i < count; i++)
                {
                    f.GetBytes(rows[i]).CopyTo(data.AsSpan(i * f.ElementSize, f.ElementSize));
                }
                return new FixedSizeColumn { Type = f.Type, Count = count, Validity = validity, ElementSize = f.ElementSize, Data = data };
            }
            case VarBytesColumn v:
            {
                ReadOnlyMemory<byte>?[] values = new ReadOnlyMemory<byte>?[count];
                for (int i = 0; i < count; i++) values[i] = v.Values[rows[i]];
                return new VarBytesColumn { Type = v.Type, Count = count, Validity = validity, Values = values };
            }
            case VariantColumn v:
            {
                DuckDbVariant?[] values = new DuckDbVariant?[count];
                for (int i = 0; i < count; i++) values[i] = v.Values[rows[i]];
                return new VariantColumn { Type = v.Type, Count = count, Validity = validity, Values = values };
            }
            case StructColumn s:
            {
                DuckDbColumn[] fields = new DuckDbColumn[s.Fields.Length];
                for (int f = 0; f < fields.Length; f++) fields[f] = Take(s.Fields[f], rows);
                return new StructColumn { Type = s.Type, Count = count, Validity = validity, Fields = fields };
            }
            case ArrayColumn a:
            {
                int size = (int)a.ArraySize;
                int[] childRows = new int[count * size];
                for (int i = 0; i < count; i++)
                {
                    for (int k = 0; k < size; k++) childRows[i * size + k] = rows[i] * size + k;
                }
                return new ArrayColumn { Type = a.Type, Count = count, Validity = validity, ArraySize = a.ArraySize, Child = Take(a.Child, childRows) };
            }
            case ListColumn l:
            {
                (ulong, ulong)[] entries = new (ulong, ulong)[count];
                List<int> childRows = [];
                for (int i = 0; i < count; i++)
                {
                    (ulong offset, ulong length) = l.Entries[rows[i]];
                    if (!l.Validity.IsValid(rows[i]))
                    {
                        entries[i] = (0, 0);
                        continue;
                    }
                    entries[i] = ((ulong)childRows.Count, length);
                    for (ulong k = 0; k < length; k++) childRows.Add(checked((int)(offset + k)));
                }
                return new ListColumn { Type = l.Type, Count = count, Validity = validity, Entries = entries, Child = Take(l.Child, childRows.ToArray()) };
            }
            default:
                throw new NotSupportedException($"Cannot gather column kind '{column.GetType().Name}'.");
        }
    }

    private static ValidityMask TakeValidity(ValidityMask validity, ReadOnlySpan<int> rows)
    {
        if (validity.IsAllValid)
        {
            return ValidityMask.AllValid;
        }
        byte[] bytes = new byte[ValidityMask.RequiredByteCount(rows.Length)];
        for (int i = 0; i < rows.Length; i++)
        {
            if (validity.IsValid(rows[i])) bytes[i >> 3] |= (byte)(1 << (i & 7));
        }
        return new ValidityMask(bytes);
    }
}
