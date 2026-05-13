using System.Runtime.InteropServices;
using Quack.Serialization;
using Quack.Types;

namespace Quack.Data;

// Port of duckdb::Vector::Deserialize for the read path.
//
// Wire layout (with SerializationCompatibility v7):
//   field 90  vector_type            (uint8 LEB128; default FLAT)
//   field 91  sel_vector or seq_start (raw / signed LEB128, depending on vector_type)
//   field 92  dict_count or seq_increment
//   field 99  geometry_format        (GEOMETRY only)
//   field 100 has_validity_mask      (bool)
//   field 101 validity               (raw bytes)
//   field 102 data                   (constant-size: raw bytes; VARCHAR/GEOM: list<string>)
//   field 103 children (STRUCT) or array_size (ARRAY)
//   field 104 list_size (LIST) or child (ARRAY)
//   field 105 entries (LIST, list<{offset, length}>)
//   field 106 child (LIST)
internal static class VectorReader
{
    public static DuckDbColumn Read(BinaryDeserializer reader, LogicalType type, int count)
    {
        VectorType vectorType = VectorType.Flat;
        if (reader.TryBeginProperty(fieldId: 90))
        {
            vectorType = (VectorType)reader.ReadByte();
        }

        switch (vectorType)
        {
            case VectorType.Flat:
                return ReadFlat(reader, type, count);
            case VectorType.Constant:
                return ExpandConstant(Read(reader, type, count: 1), count);
            case VectorType.Sequence:
                return ReadSequence(reader, type, count);
            case VectorType.Dictionary:
                return ReadDictionary(reader, type, count);
            case VectorType.Fsst:
                throw new SerializationException(
                    $"VectorType.{vectorType} is not yet supported by the MVP client.");
            default:
                throw new SerializationException($"Unknown VectorType value {(int)vectorType}.");
        }
    }

    // Dictionary vector wire layout (Vector::Serialize, DICTIONARY_VECTOR branch):
    //   field 91  sel_vector  -- raw bytes (uint32 selection index per row), LEB128-prefixed
    //   field 92  dict_count  -- idx_t (uint64 LEB128)
    //   then the inner dictionary is serialized recursively with count = dict_count
    //   and compressed_serialization = false, so it appears as a FLAT vector.
    // We materialize to a flat output column (dict[sel[i]] for each row i),
    // propagating null entries via the validity mask.
    private static DuckDbColumn ReadDictionary(BinaryDeserializer reader, LogicalType type, int count)
    {
        reader.BeginProperty(fieldId: 91);
        ReadOnlyMemory<byte> selBytes = reader.ReadDataMemory();
        int expectedSelBytes = sizeof(uint) * count;
        if (selBytes.Length != expectedSelBytes)
        {
            throw new SerializationException(
                $"Dictionary sel_vector is {selBytes.Length} bytes; expected {expectedSelBytes} ({sizeof(uint)} per row * {count} rows).");
        }

        reader.BeginProperty(fieldId: 92);
        ulong dictCount = reader.ReadUInt64();

        DuckDbColumn dictionary = Read(reader, type, checked((int)dictCount));

        return ExpandDictionary(dictionary, selBytes, count);
    }

    private static DuckDbColumn ExpandDictionary(DuckDbColumn dictionary, ReadOnlyMemory<byte> selBytes, int count)
    {
        ReadOnlySpan<uint> sel = MemoryMarshal.Cast<byte, uint>(selBytes.Span);

        // Sanity-check every index up front so we get a clear error rather
        // than a buffer overflow on a bogus selection vector.
        for (int i = 0; i < count; i++)
        {
            if (sel[i] >= (uint)dictionary.Count)
            {
                throw new SerializationException(
                    $"Dictionary sel_vector[{i}] = {sel[i]} is out of range for dictionary size {dictionary.Count}.");
            }
        }

        ValidityMask validity = BuildValidityFromDictionary(dictionary.Validity, sel, count);

        switch (dictionary)
        {
            case FixedSizeColumn fixedDict:
            {
                byte[] data = new byte[fixedDict.ElementSize * count];
                ReadOnlySpan<byte> dictData = fixedDict.Data.Span;
                int elemSize = fixedDict.ElementSize;
                for (int i = 0; i < count; i++)
                {
                    int src = (int)sel[i] * elemSize;
                    dictData.Slice(src, elemSize).CopyTo(data.AsSpan(i * elemSize, elemSize));
                }
                return new FixedSizeColumn
                {
                    Type = dictionary.Type,
                    Count = count,
                    Validity = validity,
                    Data = data,
                    ElementSize = elemSize,
                };
            }
            case StringColumn stringDict:
            {
                string?[] values = new string?[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = stringDict.Values[(int)sel[i]];
                }
                return new StringColumn
                {
                    Type = dictionary.Type,
                    Count = count,
                    Validity = validity,
                    Values = values,
                };
            }
            default:
                throw new SerializationException(
                    $"Dictionary expansion is not yet supported for column kind '{dictionary.GetType().Name}'.");
        }
    }

    private static ValidityMask BuildValidityFromDictionary(ValidityMask dictionaryValidity, ReadOnlySpan<uint> sel, int count)
    {
        if (dictionaryValidity.IsAllValid)
        {
            return ValidityMask.AllValid;
        }
        byte[] mask = new byte[ValidityMask.RequiredByteCount(count)];
        bool anyValid = false;
        for (int i = 0; i < count; i++)
        {
            if (dictionaryValidity.IsValid((int)sel[i]))
            {
                mask[i >> 3] |= (byte)(1 << (i & 7));
                anyValid = true;
            }
        }
        // If every output row turned out null, we still need to emit a mask
        // (caller can't distinguish "no rows" from "all null" otherwise).
        _ = anyValid;
        return new ValidityMask(mask);
    }

    private static DuckDbColumn ReadFlat(BinaryDeserializer reader, LogicalType type, int count)
    {
        PhysicalType physical = type.GetPhysicalType();

        // GEOMETRY has an optional `geometry_format` (field 99). Under v7 the
        // server emits WKB; on the wire we just need to consume the field.
        if (type.Id == LogicalTypeId.Geometry && reader.TryBeginProperty(fieldId: 99))
        {
            _ = reader.ReadByte(); // geometry_format
        }

        reader.BeginProperty(fieldId: 100);
        bool hasValidity = reader.ReadBool();
        ValidityMask validity = ValidityMask.AllValid;
        if (hasValidity)
        {
            reader.BeginProperty(fieldId: 101);
            // Validity is written via WriteDataPtr: a LEB128 byte-count
            // followed by the raw bitmap bytes.
            ReadOnlyMemory<byte> validityBytes = reader.ReadDataMemory();
            int expected = ValidityMask.RequiredByteCount(count);
            if (validityBytes.Length != expected)
            {
                throw new SerializationException(
                    $"Validity mask payload was {validityBytes.Length} bytes; expected {expected} for {count} rows.");
            }
            validity = new ValidityMask(validityBytes);
        }

        if (TypeIsConstantSize(physical))
        {
            int elementSize = GetPhysicalTypeSize(physical);
            reader.BeginProperty(fieldId: 102);
            // Data is also written via WriteDataPtr: LEB128 byte-count then
            // the raw column-major bytes.
            ReadOnlyMemory<byte> data = reader.ReadDataMemory();
            int expectedBytes = elementSize * count;
            if (data.Length != expectedBytes)
            {
                throw new SerializationException(
                    $"Constant-size data payload was {data.Length} bytes; expected {expectedBytes} ({elementSize} per row * {count} rows).");
            }
            return new FixedSizeColumn
            {
                Type = type,
                Count = count,
                Validity = validity,
                Data = data,
                ElementSize = elementSize,
            };
        }

        switch (physical)
        {
            case PhysicalType.Varchar:
                return ReadVarcharColumn(reader, type, count, validity);
            case PhysicalType.Struct:
                return ReadStructColumn(reader, type, count, validity);
            case PhysicalType.List:
                return ReadListColumn(reader, type, count, validity);
            case PhysicalType.Array:
                return ReadArrayColumn(reader, type, count, validity);
            case PhysicalType.Bit:
                // BIT is stored like VARCHAR on the wire — list-of-string.
                return ReadVarcharColumn(reader, type, count, validity);
            default:
                throw new SerializationException(
                    $"Unsupported physical type '{physical}' for variable-width Vector::Deserialize.");
        }
    }

    private static StringColumn ReadVarcharColumn(BinaryDeserializer reader, LogicalType type, int count, ValidityMask validity)
    {
        reader.BeginProperty(fieldId: 102);
        ulong listCount = reader.BeginList();
        if (listCount != (ulong)count)
        {
            throw new SerializationException(
                $"VARCHAR vector list count {listCount} does not match row count {count}.");
        }
        string?[] values = new string?[count];
        for (int i = 0; i < count; i++)
        {
            string raw = reader.ReadString();
            values[i] = validity.IsValid(i) ? raw : null;
        }
        reader.EndList();
        return new StringColumn { Type = type, Count = count, Validity = validity, Values = values };
    }

    private static StructColumn ReadStructColumn(BinaryDeserializer reader, LogicalType type, int count, ValidityMask validity)
    {
        if (type.TypeInfo is not StructTypeInfo structInfo)
        {
            throw new SerializationException(
                $"LogicalType '{type}' has STRUCT physical type but no StructTypeInfo.");
        }
        reader.BeginProperty(fieldId: 103);
        ulong listCount = reader.BeginList();
        if (listCount != (ulong)structInfo.ChildTypes.Count)
        {
            throw new SerializationException(
                $"STRUCT child count {listCount} does not match type's field count {structInfo.ChildTypes.Count}.");
        }
        DuckDbColumn[] fields = new DuckDbColumn[(int)listCount];
        for (int i = 0; i < fields.Length; i++)
        {
            reader.BeginObject();
            fields[i] = Read(reader, structInfo.ChildTypes[i].Value, count);
            reader.EndObject();
        }
        reader.EndList();
        return new StructColumn { Type = type, Count = count, Validity = validity, Fields = fields };
    }

    private static ListColumn ReadListColumn(BinaryDeserializer reader, LogicalType type, int count, ValidityMask validity)
    {
        if (type.TypeInfo is not ListTypeInfo listInfo)
        {
            throw new SerializationException(
                $"LogicalType '{type}' has LIST physical type but no ListTypeInfo.");
        }

        reader.BeginProperty(fieldId: 104);
        ulong listSize = reader.ReadUInt64();

        reader.BeginProperty(fieldId: 105);
        ulong entriesCount = reader.BeginList();
        if (entriesCount != (ulong)count)
        {
            throw new SerializationException(
                $"LIST entries count {entriesCount} does not match row count {count}.");
        }
        (ulong Offset, ulong Length)[] entries = new (ulong, ulong)[count];
        for (int i = 0; i < count; i++)
        {
            reader.BeginObject();
            reader.BeginProperty(fieldId: 100);
            ulong offset = reader.ReadUInt64();
            reader.BeginProperty(fieldId: 101);
            ulong length = reader.ReadUInt64();
            reader.EndObject();
            entries[i] = (offset, length);
        }
        reader.EndList();

        reader.BeginProperty(fieldId: 106);
        reader.BeginObject();
        DuckDbColumn child = Read(reader, listInfo.ChildType, checked((int)listSize));
        reader.EndObject();

        return new ListColumn
        {
            Type = type,
            Count = count,
            Validity = validity,
            Entries = entries,
            Child = child,
        };
    }

    private static ArrayColumn ReadArrayColumn(BinaryDeserializer reader, LogicalType type, int count, ValidityMask validity)
    {
        if (type.TypeInfo is not ArrayTypeInfo arrayInfo)
        {
            throw new SerializationException(
                $"LogicalType '{type}' has ARRAY physical type but no ArrayTypeInfo.");
        }
        reader.BeginProperty(fieldId: 103);
        ulong arraySize = reader.ReadUInt64();
        reader.BeginProperty(fieldId: 104);
        reader.BeginObject();
        DuckDbColumn child = Read(reader, arrayInfo.ChildType, checked((int)(arraySize * (ulong)count)));
        reader.EndObject();
        return new ArrayColumn
        {
            Type = type,
            Count = count,
            Validity = validity,
            ArraySize = (uint)arraySize,
            Child = child,
        };
    }

    private static DuckDbColumn ReadSequence(BinaryDeserializer reader, LogicalType type, int count)
    {
        reader.BeginProperty(fieldId: 91);
        long seqStart = reader.ReadInt64();
        reader.BeginProperty(fieldId: 92);
        long seqIncrement = reader.ReadInt64();

        PhysicalType physical = type.GetPhysicalType();
        if (!TypeIsConstantSize(physical))
        {
            throw new SerializationException($"SequenceVector with non-fixed physical type '{physical}' is not supported.");
        }
        int elementSize = GetPhysicalTypeSize(physical);
        byte[] data = new byte[elementSize * count];
        for (int i = 0; i < count; i++)
        {
            long value = seqStart + seqIncrement * i;
            Span<byte> slot = data.AsSpan(i * elementSize, elementSize);
            WriteFixedSizeInteger(slot, value, physical);
        }
        return new FixedSizeColumn
        {
            Type = type,
            Count = count,
            Validity = ValidityMask.AllValid,
            Data = data,
            ElementSize = elementSize,
        };
    }

    private static DuckDbColumn ExpandConstant(DuckDbColumn singleton, int count)
    {
        if (singleton.Count != 1)
        {
            throw new SerializationException("ConstantVector inner deserialization did not produce a 1-element column.");
        }
        if (singleton is FixedSizeColumn fixedCol)
        {
            byte[] expanded = new byte[fixedCol.ElementSize * count];
            ReadOnlySpan<byte> source = fixedCol.Data.Span;
            for (int i = 0; i < count; i++)
            {
                source.CopyTo(expanded.AsSpan(i * fixedCol.ElementSize, fixedCol.ElementSize));
            }
            ValidityMask expandedValidity = fixedCol.Validity.IsAllValid && fixedCol.Validity.IsValid(0)
                ? ValidityMask.AllValid
                : BroadcastValidity(fixedCol.Validity, count);
            return new FixedSizeColumn
            {
                Type = singleton.Type,
                Count = count,
                Validity = expandedValidity,
                Data = expanded,
                ElementSize = fixedCol.ElementSize,
            };
        }
        if (singleton is StringColumn stringCol)
        {
            string?[] expanded = new string?[count];
            Array.Fill(expanded, stringCol.Values[0]);
            ValidityMask expandedValidity = stringCol.Validity.IsAllValid && stringCol.Validity.IsValid(0)
                ? ValidityMask.AllValid
                : BroadcastValidity(stringCol.Validity, count);
            return new StringColumn { Type = singleton.Type, Count = count, Validity = expandedValidity, Values = expanded };
        }
        throw new SerializationException(
            $"ConstantVector broadcast not supported for column kind '{singleton.GetType().Name}'.");
    }

    private static ValidityMask BroadcastValidity(ValidityMask source, int count)
    {
        bool valid = source.IsValid(0);
        if (valid) return ValidityMask.AllValid;
        // All-null: emit a mask of the right size with all bits clear.
        byte[] mask = new byte[ValidityMask.RequiredByteCount(count)];
        return new ValidityMask(mask);
    }

    private static void WriteFixedSizeInteger(Span<byte> destination, long value, PhysicalType physical)
    {
        switch (physical)
        {
            case PhysicalType.Int8:
            case PhysicalType.UInt8:
                destination[0] = (byte)value;
                return;
            case PhysicalType.Int16:
            case PhysicalType.UInt16:
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination, (short)value);
                return;
            case PhysicalType.Int32:
            case PhysicalType.UInt32:
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, (int)value);
                return;
            case PhysicalType.Int64:
            case PhysicalType.UInt64:
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(destination, value);
                return;
            default:
                throw new SerializationException($"SequenceVector cannot generate values for physical type '{physical}'.");
        }
    }

    private static bool TypeIsConstantSize(PhysicalType physical) => physical switch
    {
        PhysicalType.Bool
            or PhysicalType.Int8 or PhysicalType.UInt8
            or PhysicalType.Int16 or PhysicalType.UInt16
            or PhysicalType.Int32 or PhysicalType.UInt32
            or PhysicalType.Int64 or PhysicalType.UInt64
            or PhysicalType.Int128 or PhysicalType.UInt128
            or PhysicalType.Float or PhysicalType.Double
            or PhysicalType.Interval => true,
        _ => false,
    };

    private static int GetPhysicalTypeSize(PhysicalType physical) => physical switch
    {
        PhysicalType.Bool or PhysicalType.Int8 or PhysicalType.UInt8 => 1,
        PhysicalType.Int16 or PhysicalType.UInt16 => 2,
        PhysicalType.Int32 or PhysicalType.UInt32 or PhysicalType.Float => 4,
        PhysicalType.Int64 or PhysicalType.UInt64 or PhysicalType.Double => 8,
        PhysicalType.Int128 or PhysicalType.UInt128 or PhysicalType.Interval => 16,
        _ => throw new SerializationException($"GetPhysicalTypeSize: physical type '{physical}' has no fixed size."),
    };
}
