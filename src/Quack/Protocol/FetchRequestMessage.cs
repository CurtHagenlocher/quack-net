using Quack.Serialization;

namespace Quack.Protocol;

public sealed record FetchRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.FetchRequest;

    public Int128 Uuid { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WriteProperty(fieldId: 1, Uuid);
    }

    internal static FetchRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        d.BeginProperty(fieldId: 1);
        return new FetchRequestMessage { Uuid = d.ReadHugeInt() };
    }
}
