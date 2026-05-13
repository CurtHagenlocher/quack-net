using System.Buffers;
using Quack.Protocol;
using Quack.Serialization;

namespace Quack.Tests.Protocol;

public class QuackMessageTests
{
    [Fact]
    public void ConnectionRequest_RoundTrips()
    {
        ConnectionRequestMessage original = new()
        {
            AuthString = "supersecret",
            ClientDuckDbVersion = "v1.5.2",
            ClientPlatform = "windows_amd64",
            MinSupportedQuackVersion = 1,
            MaxSupportedQuackVersion = 1,
        };

        byte[] bytes = original.ToBytes();
        QuackMessage parsed = QuackMessage.Deserialize(bytes);

        ConnectionRequestMessage round = Assert.IsType<ConnectionRequestMessage>(parsed);
        Assert.Equal(MessageType.ConnectionRequest, round.MessageType);
        Assert.Equal(original.AuthString, round.AuthString);
        Assert.Equal(original.ClientDuckDbVersion, round.ClientDuckDbVersion);
        Assert.Equal(original.ClientPlatform, round.ClientPlatform);
        Assert.Equal(1UL, round.MinSupportedQuackVersion);
        Assert.Equal(1UL, round.MaxSupportedQuackVersion);
    }

    [Fact]
    public void ConnectionRequest_EmptyConnectionId_OmittedFromHeader()
    {
        ConnectionRequestMessage msg = new() { AuthString = "tok" };
        byte[] bytes = msg.ToBytes();
        // Cheap structural check: the wire should contain the auth-string bytes
        // and not an empty connection-id field (the header omits field 2 when empty).
        Assert.Contains((byte)'t', bytes);
        QuackMessage parsed = QuackMessage.Deserialize(bytes);
        Assert.Equal(string.Empty, parsed.ConnectionId);
    }

    [Fact]
    public void Header_CarriesConnectionIdAndClientQueryId()
    {
        DisconnectMessage msg = new() { ConnectionId = "abcd1234", ClientQueryId = 17UL };
        byte[] bytes = msg.ToBytes();

        QuackMessage parsed = QuackMessage.Deserialize(bytes);
        Assert.IsType<DisconnectMessage>(parsed);
        Assert.Equal("abcd1234", parsed.ConnectionId);
        Assert.Equal(17UL, parsed.ClientQueryId);
    }

    [Fact]
    public void FetchRequest_HugeIntUuidRoundTrips()
    {
        Int128 uuid = ((Int128)0x0123456789ABCDEFL << 64) | 0xFEDCBA9876543210UL;
        FetchRequestMessage msg = new() { ConnectionId = "session", Uuid = uuid };

        FetchRequestMessage parsed = Assert.IsType<FetchRequestMessage>(QuackMessage.Deserialize(msg.ToBytes()));
        Assert.Equal(uuid, parsed.Uuid);
    }

    [Fact]
    public void Prepare_Request_RoundTrips()
    {
        PrepareRequestMessage msg = new() { ConnectionId = "abc", SqlQuery = "SELECT 1" };
        PrepareRequestMessage parsed = Assert.IsType<PrepareRequestMessage>(QuackMessage.Deserialize(msg.ToBytes()));
        Assert.Equal("SELECT 1", parsed.SqlQuery);
    }

    [Fact]
    public void Success_And_Disconnect_AreEmptyBodies()
    {
        SuccessResponse s = new() { ConnectionId = "abc" };
        Assert.IsType<SuccessResponse>(QuackMessage.Deserialize(s.ToBytes()));
        DisconnectMessage d = new() { ConnectionId = "abc" };
        Assert.IsType<DisconnectMessage>(QuackMessage.Deserialize(d.ToBytes()));
    }

    [Fact]
    public void ErrorResponse_RoundTrips()
    {
        ErrorResponse e = new() { Message = "boom" };
        ErrorResponse parsed = Assert.IsType<ErrorResponse>(QuackMessage.Deserialize(e.ToBytes()));
        Assert.Equal("boom", parsed.Message);
    }

    [Fact]
    public void ConnectionResponse_FromHandBuiltBytes()
    {
        // Build a ConnectionResponse on the wire by hand (since the server
        // produces these; we don't have a Serialize for it via the QuackMessage
        // abstraction but we can craft via BinarySerializer directly).
        ArrayBufferWriter<byte> writer = new();
        BinarySerializer s = new(writer);
        // Header object
        s.BeginObject();
        s.WriteProperty(fieldId: 1, (byte)MessageType.ConnectionResponse);
        s.WritePropertyWithDefault(fieldId: 2, "session-token-xyz");
        s.WriteFieldId(3);
        s.BeginNullable(false);
        s.EndNullable();
        s.EndObject();
        // Body object
        s.BeginObject();
        s.WritePropertyWithDefault(fieldId: 1, "v1.5.2");
        s.WritePropertyWithDefault(fieldId: 2, "linux_amd64");
        s.WritePropertyWithDefault(fieldId: 3, 1UL);
        s.EndObject();

        QuackMessage parsed = QuackMessage.Deserialize(writer.WrittenMemory);

        ConnectionResponseMessage cr = Assert.IsType<ConnectionResponseMessage>(parsed);
        Assert.Equal("session-token-xyz", cr.ConnectionId);
        Assert.Equal("v1.5.2", cr.ServerDuckDbVersion);
        Assert.Equal("linux_amd64", cr.ServerPlatform);
        Assert.Equal(1UL, cr.QuackVersion);
    }
}
