using Quack.Serialization;

namespace Quack.Protocol;

public sealed record SuccessResponse : QuackMessage
{
    public override MessageType MessageType => MessageType.SuccessResponse;

    internal override void SerializeBody(BinarySerializer s)
    {
        // Empty body.
    }

    internal static SuccessResponse DeserializeBody(BinaryDeserializer d) => new();
}
