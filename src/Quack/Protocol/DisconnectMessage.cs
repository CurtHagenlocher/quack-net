using Quack.Serialization;

namespace Quack.Protocol;

public sealed record DisconnectMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.DisconnectMessage;

    internal override void SerializeBody(BinarySerializer s)
    {
        // Empty body.
    }

    internal static DisconnectMessage DeserializeBody(BinaryDeserializer d) => new();
}
