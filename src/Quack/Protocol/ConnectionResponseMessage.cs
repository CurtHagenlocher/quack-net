using Quack.Serialization;

namespace Quack.Protocol;

public sealed record ConnectionResponseMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.ConnectionResponse;

    public string ServerDuckDbVersion { get; init; } = string.Empty;
    public string ServerPlatform { get; init; } = string.Empty;
    public ulong QuackVersion { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, ServerDuckDbVersion);
        s.WritePropertyWithDefault(fieldId: 2, ServerPlatform);
        s.WritePropertyWithDefault(fieldId: 3, QuackVersion);
    }

    internal static ConnectionResponseMessage DeserializeBody(BinaryDeserializer d)
    {
        string serverDuckDbVersion = string.Empty;
        string serverPlatform = string.Empty;
        ulong quackVersion = 0;

        if (d.TryBeginProperty(fieldId: 1)) serverDuckDbVersion = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) serverPlatform = d.ReadString();
        if (d.TryBeginProperty(fieldId: 3)) quackVersion = d.ReadUInt64();

        return new ConnectionResponseMessage
        {
            ServerDuckDbVersion = serverDuckDbVersion,
            ServerPlatform = serverPlatform,
            QuackVersion = quackVersion,
        };
    }
}
