using System.Text.Json;

namespace LanAdmin.Contracts;

public static class AgentMessageTypes
{
    public const string Register = "register";
    public const string Heartbeat = "heartbeat";
}

public sealed record AgentEnvelope(string Type, JsonElement Payload);

public sealed record AgentRegisterMessage(
    string AgentId,
    string HostName,
    string IpAddress,
    string MacAddress,
    string CurrentUser,
    string OsVersion,
    string AgentVersion,
    DateTimeOffset ReportedAt);

public sealed record AgentHeartbeatMessage(
    string AgentId,
    string IpAddress,
    string CurrentUser,
    DateTimeOffset ReportedAt);
