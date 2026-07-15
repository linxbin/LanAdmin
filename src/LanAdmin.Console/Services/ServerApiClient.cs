using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using LanAdmin.Contracts;

namespace LanAdmin.Console.Services;

public sealed class ServerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ServerApiClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(ConsoleRuntimeOptions.Load().ServerBaseUrl)
        };
    }

    public Uri? BaseAddress => _httpClient.BaseAddress;

    public async Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(string? search)
    {
        var path = string.IsNullOrWhiteSpace(search)
            ? "/api/devices"
            : $"/api/devices?search={Uri.EscapeDataString(search.Trim())}";
        return await GetAsync<DeviceDto>(path);
    }

    public Task<IReadOnlyList<DeviceEventDto>> GetEventsAsync(string? agentId)
    {
        var path = string.IsNullOrWhiteSpace(agentId)
            ? "/api/events"
            : $"/api/events?agentId={Uri.EscapeDataString(agentId)}";
        return GetAsync<DeviceEventDto>(path);
    }

    public Task<IReadOnlyList<DeviceGroupDto>> GetGroupsAsync()
    {
        return GetAsync<DeviceGroupDto>("/api/groups");
    }

    public async Task CreateGroupAsync(string name)
    {
        var payload = JsonSerializer.Serialize(new CreateGroupRequest(name), _jsonOptions);
        using var response = await _httpClient.PostAsync("/api/groups", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task RenameGroupAsync(long groupId, string name)
    {
        var payload = JsonSerializer.Serialize(new RenameGroupRequest(name), _jsonOptions);
        using var response = await _httpClient.PutAsync($"/api/groups/{groupId}", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteGroupAsync(long groupId)
    {
        using var response = await _httpClient.DeleteAsync($"/api/groups/{groupId}");
        response.EnsureSuccessStatusCode();
    }

    public async Task AssignGroupAsync(string agentId, long? groupId)
    {
        var payload = JsonSerializer.Serialize(new AssignGroupRequest(groupId), _jsonOptions);
        using var response = await _httpClient.PostAsync($"/api/devices/{Uri.EscapeDataString(agentId)}/assign-group", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task AssignGroupsAsync(IReadOnlyList<string> agentIds, long? groupId)
    {
        var payload = JsonSerializer.Serialize(new BatchAssignGroupRequest(agentIds, groupId), _jsonOptions);
        using var response = await _httpClient.PostAsync("/api/devices/assign-group-batch", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task SetShutdownThresholdAsync(string agentId, int shutdownThresholdDays)
    {
        var payload = JsonSerializer.Serialize(new SetShutdownThresholdRequest(shutdownThresholdDays), _jsonOptions);
        using var response = await _httpClient.PostAsync($"/api/devices/{Uri.EscapeDataString(agentId)}/shutdown-threshold", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task SetShutdownThresholdsAsync(IReadOnlyList<string> agentIds, int shutdownThresholdDays)
    {
        var payload = JsonSerializer.Serialize(new BatchSetShutdownThresholdRequest(agentIds, shutdownThresholdDays), _jsonOptions);
        using var response = await _httpClient.PostAsync("/api/devices/shutdown-threshold-batch", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task<BatchPromptShutdownReminderResult> PromptShutdownReminderAsync(IReadOnlyList<string> agentIds)
    {
        var payload = JsonSerializer.Serialize(new BatchPromptShutdownReminderRequest(agentIds), _jsonOptions);
        using var response = await _httpClient.PostAsync("/api/devices/prompt-shutdown-reminder-batch", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<BatchPromptShutdownReminderResult>(stream, _jsonOptions);
        return result ?? new BatchPromptShutdownReminderResult(agentIds.Count, 0, agentIds.ToList());
    }

    public async Task DeleteDeviceAsync(string agentId)
    {
        using var response = await _httpClient.DeleteAsync($"/api/devices/{Uri.EscapeDataString(agentId)}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<ReminderStyleDto> GetReminderStyleAsync()
    {
        using var response = await _httpClient.GetAsync("/api/reminder-style");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<ReminderStyleDto>(stream, _jsonOptions)
               ?? ReminderStyleDefaults.CreateDefault();
    }

    public async Task<ReminderStyleDto> SaveReminderStyleAsync(ReminderStyleDto style)
    {
        var payload = JsonSerializer.Serialize(style, _jsonOptions);
        using var response = await _httpClient.PutAsync("/api/reminder-style", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<ReminderStyleDto>(stream, _jsonOptions)
               ?? style;
    }

    public async Task<ReminderBackgroundImageUploadResult> UploadReminderBackgroundImageAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _httpClient.PostAsync("/api/reminder-style/background", content);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<ReminderBackgroundImageUploadResult>(responseStream, _jsonOptions)
               ?? new ReminderBackgroundImageUploadResult("");
    }

    private async Task<IReadOnlyList<T>> GetAsync<T>(string path)
    {
        using var response = await _httpClient.GetAsync(path);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<List<T>>(stream, _jsonOptions);
        return result ?? new List<T>();
    }
}

internal sealed class ConsoleRuntimeOptions
{
    public string ServerBaseUrl { get; init; } = "http://localhost:5000";

    public static ConsoleRuntimeOptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ConsoleRuntimeOptions();
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("Console", out var consoleNode))
        {
            return new ConsoleRuntimeOptions();
        }

        return new ConsoleRuntimeOptions
        {
            ServerBaseUrl = consoleNode.TryGetProperty("ServerBaseUrl", out var url)
                ? url.GetString() ?? "http://localhost:5000"
                : "http://localhost:5000"
        };
    }
}
