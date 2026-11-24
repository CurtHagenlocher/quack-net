// Copyright (c) Curt Hagenlocher. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Write path for one Vector. Mirrors duckdb::Vector::Serialize but only emits
// FLAT vectors — we leave compression to the server (`compressed_serialization`
// would be false in the C++ call, which skips the field 90 vector_type tag).
internal static class VectorWriter
{
    public static void Write(BinarySerializer s, DuckDbColumn column, int count)
        => Write(s, column, count, ValidityMask.AllValid);

    // `parentValidity` is the validity of the enclosing STRUCT rows. DuckDB
    // requires the children of a NULL struct row to be NULL as well (a field
    // read goes to the child vector directly), while Arrow and hand-built
    // columns may leave them valid, so the effective mask is child AND parent.
    private static void Write(BinarySerializer s, DuckDbColumn column, int count, ValidityMask parentValidity)
    {
        if (column.Count != count)
        {
            throw new ArgumentException(
                $"Column count ({column.Count}) does not match the chunk row count ({count}).", nameof(column));
        }

        // GEOMETRY carries an optional `geometry_format` byte at field 99 that
        // tells the server whether the column payload is plain WKB (1) or
        // duckdb's native double-aligned SPATIAL format (0). If we omit it
        // the server defaults to SPATIAL and mis-parses our WKB bytes.
        if (column.Type.Id == LogicalTypeId.Geometry)
        {
            s.WriteProperty(fieldId: 99, (byte)GeometryStorageType.Wkb);
        }

        if (!column.Validity.IsAllValid && column.Validity.Bytes.Length != ValidityMask.RequiredByteCount(count))
        {
            throw new SerializationException(
                $"Validity mask is {column.Validity.Bytes.Length} bytes; expected {ValidityMask.RequiredByteCount(count)} for {count} rows.");
        }
        ValidityMask validity = Combine(column.Validity, parentValidity, count);
        bool hasValidity = !validity.IsAllValid;
        s.WriteProperty(fieldId: 100, hasValidity);
        if (hasValidity)
        {
            s.WriteFieldId(101);
            s.WriteBlob(validity.Bytes.Span);
        }

        switch (column)
        {
            case FixedSizeColumn fixedCol:
                WriteFixedSizeColumn(s, fixedCol, count);
                break;
            case VarBytesColumn bytesCol:
                WriteVarBytesColumn(s, bytesCol, count, validity);
                break;
            case StructColumn structCol:
                WriteStructColumn(s, structCol, count, validity);
                break;
            case ListColumn listCol:
                WriteListColumn(s, listCol, count);
                break;
            case ArrayColumn arrayCol:
                WriteArrayColumn(s, arrayCol, count);
                break;
            default:
                throw new SerializationException(
                    $"VectorWriter does not support column kind '{column.GetType().Name}'.");
        }
    }

    private static void WriteFixedSizeColumn(BinarySerializer s, FixedSizeColumn col, int count)
    {
        int expectedBytes = col.ElementSize * count;
        if (col.Data.Length != expectedBytes)
        {
            throw new SerializationException(
                $"FixedSizeColumn data is {col.Data.Length} bytes; expected {expectedBytes} ({col.ElementSize} * {count}).");
        }
        s.WriteFieldId(102);
        s.WriteBlob(col.Data.Span);
    }

    private static ValidityMask Combine(ValidityMask own, ValidityMask parent, int count)
    {
        if (parent.IsAllValid)
        {
            return own;
        }
        if (own.IsAllValid)
        {
            return parent;
        }
        byte[] bytes = new byte[ValidityMask.RequiredByteCount(count)];
        ReadOnlySpan<byte> a = own.Bytes.Span;
        ReadOnlySpan<byte> b = parent.Bytes.Span;
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(a[i] & b[i]);
        }
        return new ValidityMask(bytes);
    }

    private static void WriteVarBytesColumn(BinarySerializer s, VarBytesColumn col, int count, ValidityMask validity)
    {
        if (col.Values.Length != count)
        {
            throw new SerializationException(
                $"VarBytesColumn has {col.Values.Length} values; expected {count}.");
        }
        if (col.Type.Id != LogicalTypeId.Geometry)
        {
            WriteVarBytesBlobs(s, col, count, validity);
            return;
        }
        // GEOMETRY keeps the list form in DuckDB 2.0 (field 102, list<WKB>).
        s.WriteFieldId(102);
        s.BeginList((ulong)count);
        for (int i = 0; i < count; i++)
        {
            // Null entries are represented by the validity mask; the slot
            // itself is written as an empty payload (matching duckdb's
            // NullValue<string_t>() which is empty).
            ReadOnlySpan<byte> payload = col.Values[i].GetValueOrDefault().Span;
            s.WriteBlob(payload);
        }
        s.EndList();
    }

    // DuckDB 2.0 string layout (StorageVersion V2_0_0): field 107 total byte
    // count, field 108 one uint32 length per row (0 for NULL rows), field 109
    // the non-NULL payloads back to back.
    // A NULL row (by the effective mask) must contribute no bytes: DuckDB skips
    // NULL rows when it walks byte_data.
    private static void WriteVarBytesBlobs(BinarySerializer s, VarBytesColumn col, int count, ValidityMask validity)
    {
        byte[] lengths = new byte[sizeof(uint) * count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int length = validity.IsValid(i) ? col.Values[i].GetValueOrDefault().Length : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(lengths.AsSpan(i * sizeof(uint), sizeof(uint)), (uint)length);
            total += length;
        }
        byte[] bytes = new byte[checked((int)total)];
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            if (!validity.IsValid(i))
            {
                continue;
            }
            ReadOnlySpan<byte> payload = col.Values[i].GetValueOrDefault().Span;
            payload.CopyTo(bytes.AsSpan(offset));
            offset += payload.Length;
        }
        s.WriteProperty(fieldId: 107, (ulong)total);
        s.WriteFieldId(108);
        s.WriteBlob(lengths);
        s.WriteFieldId(109);
        s.WriteBlob(bytes);
    }

    private static void WriteStructColumn(BinarySerializer s, StructColumn col, int count, ValidityMask validity)
    {
        s.WriteFieldId(103);
        s.BeginList((ulong)col.Fields.Length);
        foreach (DuckDbColumn child in col.Fields)
        {
            s.BeginObject();
            Write(s, child, count, validity);
            s.EndObject();
        }
        s.EndList();
    }

    private static void WriteListColumn(BinarySerializer s, ListColumn col, int count)
    {
        if (col.Entries.Length != count)
        {
            throw new SerializationException(
                $"ListColumn has {col.Entries.Length} entries; expected {count}.");
        }
        s.WriteProperty(fieldId: 104, (ulong)col.Child.Count);

        s.WriteFieldId(105);
        s.BeginList((ulong)count);
        for (int i = 0; i < count; i++)
        {
            (ulong offset, ulong length) = col.Entries[i];
            s.BeginObject();
            s.WriteProperty(fieldId: 100, offset);
            s.WriteProperty(fieldId: 101, length);
            s.EndObject();
        }
        s.EndList();

        s.WriteFieldId(106);
        s.BeginObject();
        Write(s, col.Child, col.Child.Count);
        s.EndObject();
    }

    private static void WriteArrayColumn(BinarySerializer s, ArrayColumn col, int count)
    {
        s.WriteProperty(fieldId: 103, (ulong)col.ArraySize);
        s.WriteFieldId(104);
        s.BeginObject();
        Write(s, col.Child, col.Child.Count);
        s.EndObject();
    }
}
