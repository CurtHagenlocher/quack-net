using Quack.Serialization;

namespace Quack.Protocol;

public sealed record PrepareRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.PrepareRequest;

    public string SqlQuery { get; init; } = string.Empty;

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, SqlQuery);
    }

    internal static PrepareRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        string sql = string.Empty;
        if (d.TryBeginProperty(fieldId: 1)) sql = d.ReadString();
        return new PrepareRequestMessage { SqlQuery = sql };
    }
}
