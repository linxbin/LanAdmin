namespace LanAdmin.Contracts;

public static class BootstrapProtocol
{
    public const int Version = 1;
}

public sealed record AgentBootstrapResponse(
    string ServerBaseUrl,
    string AgentWebSocketUrl,
    int HeartbeatSeconds);
