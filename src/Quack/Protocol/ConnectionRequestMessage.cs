using Quack.Serialization;

namespace Quack.Protocol;

public sealed record ConnectionRequestMessage : QuackMessage
{
    public override MessageType MessageType => MessageType.ConnectionRequest;

    public string AuthString { get; init; } = string.Empty;
    public string ClientDuckDbVersion { get; init; } = string.Empty;
    public string ClientPlatform { get; init; } = string.Empty;
    public ulong MinSupportedQuackVersion { get; init; }
    public ulong MaxSupportedQuackVersion { get; init; }

    internal override void SerializeBody(BinarySerializer s)
    {
        s.WritePropertyWithDefault(fieldId: 1, AuthString);
        s.WritePropertyWithDefault(fieldId: 2, ClientDuckDbVersion);
        s.WritePropertyWithDefault(fieldId: 3, ClientPlatform);
        s.WritePropertyWithDefault(fieldId: 4, MinSupportedQuackVersion);
        s.WritePropertyWithDefault(fieldId: 5, MaxSupportedQuackVersion);
    }

    internal static ConnectionRequestMessage DeserializeBody(BinaryDeserializer d)
    {
        string authString = string.Empty;
        string clientDuckDbVersion = string.Empty;
        string clientPlatform = string.Empty;
        ulong minVersion = 0;
        ulong maxVersion = 0;

        if (d.TryBeginProperty(fieldId: 1)) authString = d.ReadString();
        if (d.TryBeginProperty(fieldId: 2)) clientDuckDbVersion = d.ReadString();
        if (d.TryBeginProperty(fieldId: 3)) clientPlatform = d.ReadString();
        if (d.TryBeginProperty(fieldId: 4)) minVersion = d.ReadUInt64();
        if (d.TryBeginProperty(fieldId: 5)) maxVersion = d.ReadUInt64();

        return new ConnectionRequestMessage
        {
            AuthString = authString,
            ClientDuckDbVersion = clientDuckDbVersion,
            ClientPlatform = clientPlatform,
            MinSupportedQuackVersion = minVersion,
            MaxSupportedQuackVersion = maxVersion,
        };
    }
}
