using Quack.Data;
using Quack.Serialization;

namespace Quack.Protocol;

public sealed record FetchResponseMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.FetchResponse;

    public IReadOnlyList<DuckDbChunk> Results { get; init; } = [];
    public ulong? BatchIndex { get; init; }

    public bool IsEndOfStream => Results.Count == 0;

    internal override void SerializeBody(BinarySerializer s)
    {
        throw new NotSupportedException("FetchResponseMessage serialization is server-side only.");
    }

    internal static FetchResponseMessage DeserializeBody(BinaryDeserializer d)
    {
        List<DuckDbChunk> results = [];
        if (d.TryBeginProperty(fieldId: 1))
        {
            ulong count = d.BeginList();
            for (ulong i = 0; i < count; i++)
            {
                DuckDbChunk? chunk = DataChunkWrapper.ReadChunk(d);
                if (chunk is not null) results.Add(chunk);
            }
            d.EndList();
        }

        // batch_index is optional_idx -> a single uint64 LEB128 with
        // ulong.MaxValue meaning "invalid".
        d.BeginProperty(fieldId: 2);
        ulong raw = d.ReadUInt64();
        ulong? batchIndex = raw == MessageHeader.OptionalIdxInvalid ? null : raw;

        return new FetchResponseMessage
        {
            Results = results,
            BatchIndex = batchIndex,
        };
    }
}
