using System.Text.Json;

namespace LanAgent;

internal sealed record AgentNotifierState(
    string AgentId,
    string HostName,
    string CurrentUser,
    long UptimeSeconds,
    int ShutdownThresholdDays,
    DateTimeOffset LastUpdatedAt);

internal sealed record AgentReminderState(
    DateTimeOffset? LastShownAt,
    DateTimeOffset? SnoozeUntil);

internal static class AgentNotifierFormatting
{
    public static string FormatUptime(long uptimeSeconds)
    {
        var uptime = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}天 {uptime.Hours}小时 {uptime.Minutes}分钟";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分钟";
        }

        return $"{Math.Max(0, (int)uptime.TotalMinutes)}分钟";
    }
}

internal static class AgentNotifierStateStore
{
    private static readonly string StatePath = Path.Combine(AgentStoragePaths.AgentDirectory, "notifier-state.json");

    public static AgentNotifierState? Load()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(StatePath);
            return JsonSerializer.Deserialize<AgentNotifierState>(stream, AgentJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AgentNotifierState state)
    {
        Directory.CreateDirectory(AgentStoragePaths.AgentDirectory);
        var json = JsonSerializer.Serialize(state, AgentJson.Options);
        File.WriteAllText(StatePath, json);
    }
}

internal static class AgentReminderStateStore
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanAdmin",
        "AgentNotifier");

    private static readonly string StatePath = Path.Combine(StateDirectory, "reminder-state.json");

    public static AgentReminderState Load()
    {
        if (!File.Exists(StatePath))
        {
            return new AgentReminderState(null, null);
        }

        try
        {
            using var stream = File.OpenRead(StatePath);
            return JsonSerializer.Deserialize<AgentReminderState>(stream, AgentJson.Options)
                   ?? new AgentReminderState(null, null);
        }
        catch
        {
            return new AgentReminderState(null, null);
        }
    }

    public static void Save(AgentReminderState state)
    {
        Directory.CreateDirectory(StateDirectory);
        var json = JsonSerializer.Serialize(state, AgentJson.Options);
        File.WriteAllText(StatePath, json);
    }
}
