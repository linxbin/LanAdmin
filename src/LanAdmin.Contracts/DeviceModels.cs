namespace LanAdmin.Contracts;

public static class ShutdownThresholdDefaults
{
    public const int DefaultDays = 7;
    public const int MinDays = 1;
    public const int MaxDays = 3650;
}

public enum DeviceStatus
{
    Offline = 0,
    Online = 1
}

public enum DeviceEventType
{
    Registered = 0,
    Online = 1,
    Offline = 2,
    GroupChanged = 3,
    ShutdownThresholdChanged = 4
}

public sealed record DeviceDto(
    string AgentId,
    string HostName,
    string IpAddress,
    string MacAddress,
    string CurrentUser,
    string OsVersion,
    string AgentVersion,
    DeviceStatus Status,
    DateTimeOffset LastSeenAt,
    string? GroupName,
    long UptimeSeconds,
    int ShutdownThresholdDays);

public sealed record DeviceEventDto(
    long Id,
    string AgentId,
    DeviceEventType EventType,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record DeviceGroupDto(
    long Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateGroupRequest(string Name);

public sealed record RenameGroupRequest(string Name);

public sealed record AssignGroupRequest(long? GroupId);

public sealed record BatchAssignGroupRequest(IReadOnlyList<string> AgentIds, long? GroupId);

public sealed record SetShutdownThresholdRequest(int ShutdownThresholdDays);

public sealed record BatchSetShutdownThresholdRequest(IReadOnlyList<string> AgentIds, int ShutdownThresholdDays);
