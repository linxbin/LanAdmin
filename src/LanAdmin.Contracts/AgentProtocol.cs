using System.Text.Json;

namespace LanAdmin.Contracts;

public static class AgentMessageTypes
{
    public const string Register = "register";
    public const string Heartbeat = "heartbeat";
}

public static class ServerMessageTypes
{
    public const string Configuration = "configuration";
    public const string ManualShutdownReminder = "manualShutdownReminder";
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
    long UptimeSeconds,
    DateTimeOffset ReportedAt);

public sealed record AgentHeartbeatMessage(
    string AgentId,
    string IpAddress,
    string CurrentUser,
    long UptimeSeconds,
    DateTimeOffset ReportedAt);

public sealed record AgentConfigurationMessage(
    string AgentId,
    int ShutdownThresholdDays,
    DateTimeOffset ReportedAt);

public sealed record ManualShutdownReminderMessage(
    string AgentId,
    string CommandId,
    DateTimeOffset RequestedAt);
