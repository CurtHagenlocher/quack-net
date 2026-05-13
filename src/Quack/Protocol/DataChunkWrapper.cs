using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

// Mirror of duckdb_quack::DataChunkWrapper. When stored as an element of
// `vector<unique_ptr<DataChunkWrapper>>` the wire framing is:
//   - 1 byte present  (Serializer::WriteValue(T*) -> OnNullableBegin)
//   - (if present)
//     - object begin (no bytes)  -- Serializer::WriteValue<T-has-Serialize>
//     - field 300 "chunk"        (DataChunkWrapper::Serialize -> WriteObject)
//     - inner object begin (no bytes)
//     - DataChunk fields (100=rows, 101=types, 102=columns)
//     - inner terminator (0xFFFF)
//     - outer terminator (0xFFFF)
internal static class DataChunkWrapper
{
    public static DuckDbChunk? ReadChunk(BinaryDeserializer d)
    {
        bool present = d.BeginNullable();
        if (!present)
        {
            d.EndNullable();
            return null;
        }
        d.BeginObject();
        d.BeginProperty(fieldId: 300);
        DuckDbChunk chunk = DataChunkReader.Read(d);
        d.EndObject();
        d.EndNullable();
        return chunk;
    }

    public static void WriteChunk(BinarySerializer s, DuckDbChunk? chunk)
    {
        s.BeginNullable(chunk is not null);
        if (chunk is null)
        {
            s.EndNullable();
            return;
        }
        // Outer object from WriteValue<T-has-Serialize> wrapping.
        s.BeginObject();
        // field 300 "chunk" — DataChunkWrapper::Serialize uses WriteObject(300, ...)
        // which writes field id + BeginObject + DataChunk.Serialize + EndObject.
        s.WriteFieldId(300);
        DataChunkWriter.Write(s, chunk);
        s.EndObject();
        s.EndNullable();
    }
}
