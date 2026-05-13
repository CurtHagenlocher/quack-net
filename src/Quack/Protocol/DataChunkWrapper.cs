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
}
