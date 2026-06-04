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

internal sealed record AgentManualReminderRequest(
    string CommandId,
    DateTimeOffset RequestedAt);

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

internal static class AgentManualReminderSignal
{
    private const string SignalName = @"Global\LanAdmin.AgentManualReminder";

    public static EventWaitHandle OpenOrCreate()
    {
        return new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
    }

    public static void Notify()
    {
        using var signal = OpenOrCreate();
        signal.Set();
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
            using var stream = File.Open(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<AgentNotifierState>(stream, AgentJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AgentNotifierState state)
    {
        var json = JsonSerializer.Serialize(state, AgentJson.Options);
        JsonFileWriter.WriteAtomically(StatePath, json);
    }
}

internal static class AgentManualReminderRequestStore
{
    private static readonly string StatePath = Path.Combine(AgentStoragePaths.AgentDirectory, "manual-reminder.json");

    public static void Save(AgentManualReminderRequest request)
    {
        var json = JsonSerializer.Serialize(request, AgentJson.Options);
        JsonFileWriter.WriteAtomically(StatePath, json);
    }

    public static AgentManualReminderRequest? Load()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<AgentManualReminderRequest>(stream, AgentJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
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
            using var stream = File.Open(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        var json = JsonSerializer.Serialize(state, AgentJson.Options);
        JsonFileWriter.WriteAtomically(StatePath, json);
    }
}

internal static class JsonFileWriter
{
    public static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(true);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
