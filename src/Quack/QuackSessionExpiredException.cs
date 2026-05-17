namespace Quack;

// Thrown when the server reports that the client's connection_id is no
// longer known to it — usually because the server was restarted (its
// in-memory session map is wiped) but also possible if the session was
// disconnected out of band. The transport itself is still healthy; only
// the logical session is gone. QuackConnection's opt-in auto-reconnect
// catches this internally; consumers who left auto-reconnect off see it
// propagate.
public sealed class QuackSessionExpiredException : QuackException
{
    public QuackSessionExpiredException(string connectionId, string serverMessage)
        : base($"Quack server reports the session is no longer valid (connection_id={connectionId}): {serverMessage}")
    {
        ConnectionId = connectionId;
        ServerMessage = serverMessage;
    }

    public string ConnectionId { get; }
    public string ServerMessage { get; }
}
