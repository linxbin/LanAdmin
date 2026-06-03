namespace LanAdmin.Contracts;

public static class BootstrapProtocol
{
    public const string DiscoveryRequestType = "lanadmin-discovery";
    public const string DiscoveryResponseType = "lanadmin-discovery-response";
    public const int Version = 1;
}

public sealed record AgentBootstrapResponse(
    string ServerBaseUrl,
    string AgentWebSocketUrl,
    int HeartbeatSeconds);

public sealed record AgentDiscoveryRequest(
    string Type,
    int Version);

public sealed record AgentDiscoveryResponse(
    string Type,
    int Version,
    string ServerBaseUrl);
