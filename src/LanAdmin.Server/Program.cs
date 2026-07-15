using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LanAdmin.Contracts;
using LanAdmin.Server.Diagnostics;
using LanAdmin.Server;
using LanAdmin.Server.Data;
using LanAdmin.Server.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var fileLoggerOptions = builder.Configuration.GetSection("FileLogging").Get<FileLoggerOptions>() ?? new FileLoggerOptions();

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "LanAdmin Server";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new FileLoggerProvider(fileLoggerOptions));

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("Bootstrap"));
builder.Services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddHostedService<OfflineDeviceMonitor>();

var app = builder.Build();

app.UseWebSockets();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
    await repository.InitializeAsync();
}

app.MapGet("/api/reminder-style", async (IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var style = await repository.GetReminderStyleAsync(cancellationToken);
    return Results.Ok(style);
});

app.MapPut("/api/reminder-style", async (ReminderStyleDto request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (!ReminderStyleValidator.TryNormalize(request, out var style, out var errorMessage))
    {
        return Results.BadRequest(errorMessage);
    }

    var saved = await repository.SetReminderStyleAsync(style, cancellationToken);
    return Results.Ok(saved);
});

app.MapGet("/api/reminder-style/background", () =>
{
    var imagePath = GetReminderBackgroundImagePath();
    return File.Exists(imagePath)
        ? Results.File(imagePath, "application/octet-stream")
        : Results.NotFound();
});

app.MapPost("/api/reminder-style/background", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    const long maxImageBytes = 2 * 1024 * 1024;

    try
    {
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(request.Body, buffer, maxImageBytes, cancellationToken);
        if (buffer.Length == 0)
        {
            return Results.BadRequest("Background image is required.");
        }

        if (!IsSupportedImage(buffer.ToArray()))
        {
            return Results.BadRequest("Background image must be a valid PNG, JPG, GIF, or BMP file.");
        }

        var imagePath = GetReminderBackgroundImagePath();
        var directory = Path.GetDirectoryName(imagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = imagePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, buffer.ToArray(), cancellationToken);

        if (File.Exists(imagePath))
        {
            File.Replace(tempPath, imagePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, imagePath);
        }

        var url = $"/api/reminder-style/background?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return Results.Ok(new ReminderBackgroundImageUploadResult(url));
    }
    catch (IOException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/devices", async (string? search, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var devices = await repository.GetDevicesAsync(search, cancellationToken);
    return Results.Ok(devices);
});

app.MapGet("/api/events", async (string? agentId, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var events = await repository.GetDeviceEventsAsync(agentId, cancellationToken);
    return Results.Ok(events);
});

app.MapGet("/api/groups", async (IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var groups = await repository.GetGroupsAsync(cancellationToken);
    return Results.Ok(groups);
});

app.MapPost("/api/groups", async (CreateGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Group name is required.");
    }

    try
    {
        var group = await repository.CreateGroupAsync(request.Name.Trim(), cancellationToken);
        return Results.Ok(group);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPut("/api/groups/{groupId:long}", async (long groupId, RenameGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Group name is required.");
    }

    try
    {
        var group = await repository.RenameGroupAsync(groupId, request.Name.Trim(), cancellationToken);
        return group is null ? Results.NotFound() : Results.Ok(group);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapDelete("/api/groups/{groupId:long}", async (long groupId, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var deleted = await repository.DeleteGroupAsync(groupId, cancellationToken);
    return deleted ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/devices/{agentId}/assign-group", async (string agentId, AssignGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var updated = await repository.AssignGroupAsync(agentId, request.GroupId, cancellationToken);
    return updated ? Results.Ok() : Results.NotFound();
});

app.MapPost("/api/devices/assign-group-batch", async (BatchAssignGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (request.AgentIds is null || request.AgentIds.Count == 0)
    {
        return Results.BadRequest("At least one device must be selected.");
    }

    var updated = await repository.AssignGroupsAsync(request.AgentIds, request.GroupId, cancellationToken);
    return Results.Ok(new { updated });
});

app.MapPost("/api/devices/{agentId}/shutdown-threshold", async (string agentId, SetShutdownThresholdRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (!TryValidateShutdownThresholdDays(request.ShutdownThresholdDays, out var errorMessage))
    {
        return Results.BadRequest(errorMessage);
    }

    try
    {
        var updated = await repository.SetShutdownThresholdAsync(agentId, request.ShutdownThresholdDays, cancellationToken);
        return updated ? Results.Ok() : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/devices/shutdown-threshold-batch", async (BatchSetShutdownThresholdRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (request.AgentIds is null || request.AgentIds.Count == 0)
    {
        return Results.BadRequest("At least one device must be selected.");
    }

    if (!TryValidateShutdownThresholdDays(request.ShutdownThresholdDays, out var errorMessage))
    {
        return Results.BadRequest(errorMessage);
    }

    try
    {
        var updated = await repository.SetShutdownThresholdsAsync(request.AgentIds, request.ShutdownThresholdDays, cancellationToken);
        return Results.Ok(new { updated });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPost("/api/devices/prompt-shutdown-reminder-batch", async (
    BatchPromptShutdownReminderRequest request,
    AgentConnectionRegistry connections,
    IDeviceRepository repository,
    CancellationToken cancellationToken) =>
{
    if (request.AgentIds is null || request.AgentIds.Count == 0)
    {
        return Results.BadRequest("At least one device must be selected.");
    }

    var normalizedAgentIds = request.AgentIds
        .Where(agentId => !string.IsNullOrWhiteSpace(agentId))
        .Select(agentId => agentId.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (normalizedAgentIds.Count == 0)
    {
        return Results.BadRequest("At least one device must be selected.");
    }

    var offlineAgentIds = new List<string>();
    var sentCount = 0;

    foreach (var agentId in normalizedAgentIds)
    {
        if (!await connections.SendManualShutdownReminderAsync(agentId, cancellationToken))
        {
            offlineAgentIds.Add(agentId);
            continue;
        }

        sentCount++;
        await repository.AddDeviceEventAsync(
            agentId,
            DeviceEventType.ManualShutdownReminderRequested,
            "管理员手动触发了关机提醒。",
            cancellationToken);
    }

    return Results.Ok(new BatchPromptShutdownReminderResult(
        normalizedAgentIds.Count,
        sentCount,
        offlineAgentIds));
});

app.MapDelete("/api/devices/{agentId}", async (string agentId, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var deleted = await repository.DeleteDeviceAsync(agentId, cancellationToken);
    return deleted ? Results.Ok() : Results.NotFound();
});

app.MapGet("/api/bootstrap/agent", (IOptions<BootstrapOptions> bootstrapOptions, IOptions<AgentOptions> agentOptions) =>
{
    var serverBaseUrl = bootstrapOptions.Value.ServerBaseUrl.TrimEnd('/');
    var response = new AgentBootstrapResponse(
        serverBaseUrl,
        BuildAgentWebSocketUrl(serverBaseUrl),
        agentOptions.Value.HeartbeatSeconds);

    return Results.Ok(response);
});

app.Map("/ws/agent", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var repository = context.RequestServices.GetRequiredService<IDeviceRepository>();
    var connections = context.RequestServices.GetRequiredService<AgentConnectionRegistry>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentSocket");

    var buffer = new byte[16 * 1024];
    string? connectedAgentId = null;

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var payload = await ReceiveMessageAsync(webSocket, buffer, context.RequestAborted);
            if (payload is null)
            {
                break;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<AgentEnvelope>(payload, JsonDefaults.Options);
                if (envelope is null)
                {
                    continue;
                }

                switch (envelope.Type)
                {
                    case AgentMessageTypes.Register:
                    {
                        var message = envelope.Payload.Deserialize<AgentRegisterMessage>(JsonDefaults.Options);
                        if (message is not null)
                        {
                            connectedAgentId = message.AgentId;
                            connections.Bind(message.AgentId, webSocket);
                            await repository.UpsertRegistrationAsync(message, context.RequestAborted);
                            await SendAgentConfigurationAsync(webSocket, repository, message.AgentId, context.RequestAborted);
                        }

                        break;
                    }
                    case AgentMessageTypes.Heartbeat:
                    {
                        var message = envelope.Payload.Deserialize<AgentHeartbeatMessage>(JsonDefaults.Options);
                        if (message is not null)
                        {
                            connectedAgentId ??= message.AgentId;
                            connections.Bind(message.AgentId, webSocket);
                            await repository.RecordHeartbeatAsync(message, context.RequestAborted);
                            await SendAgentConfigurationAsync(webSocket, repository, message.AgentId, context.RequestAborted);
                        }

                        break;
                    }
                    default:
                        logger.LogWarning("Unknown agent message type: {Type}", envelope.Type);
                        break;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid agent payload received.");
            }
        }
    }
    finally
    {
        if (!string.IsNullOrWhiteSpace(connectedAgentId))
        {
            connections.Unbind(connectedAgentId, webSocket);
        }
    }
});

await app.RunAsync();

static string BuildAgentWebSocketUrl(string serverBaseUrl)
{
    var baseUri = new Uri(serverBaseUrl, UriKind.Absolute);
    var builder = new UriBuilder(baseUri)
    {
        Scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        Path = "/ws/agent"
    };

    if ((builder.Scheme == "ws" && baseUri.Port == 80) || (builder.Scheme == "wss" && baseUri.Port == 443))
    {
        builder.Port = -1;
    }

    return builder.Uri.ToString();
}

static bool TryValidateShutdownThresholdDays(int shutdownThresholdDays, out string? errorMessage)
{
    if (shutdownThresholdDays < ShutdownThresholdDefaults.MinDays || shutdownThresholdDays > ShutdownThresholdDefaults.MaxDays)
    {
        errorMessage = $"Shutdown threshold must be between {ShutdownThresholdDefaults.MinDays} and {ShutdownThresholdDefaults.MaxDays} days.";
        return false;
    }

    errorMessage = null;
    return true;
}

static async Task SendAgentConfigurationAsync(WebSocket webSocket, IDeviceRepository repository, string agentId, CancellationToken cancellationToken)
{
    var shutdownThresholdDays = await repository.GetShutdownThresholdDaysAsync(agentId, cancellationToken);
    if (shutdownThresholdDays is null || webSocket.State != WebSocketState.Open)
    {
        return;
    }

    var reminderStyle = await repository.GetReminderStyleAsync(cancellationToken);
    var payload = new AgentConfigurationMessage(
        agentId,
        shutdownThresholdDays.Value,
        DateTimeOffset.UtcNow,
        reminderStyle);

    await SendSocketEnvelopeAsync(webSocket, ServerMessageTypes.Configuration, payload, cancellationToken);
}

static async Task SendSocketEnvelopeAsync<T>(WebSocket webSocket, string type, T payload, CancellationToken cancellationToken)
{
    var envelope = new { type, payload };
    var json = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
    var bytes = Encoding.UTF8.GetBytes(json);
    await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task<string?> ReceiveMessageAsync(WebSocket webSocket, byte[] buffer, CancellationToken cancellationToken)
{
    using var stream = new MemoryStream();

    while (true)
    {
        var result = await webSocket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            return null;
        }

        stream.Write(buffer, 0, result.Count);

        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}

static string GetReminderBackgroundImagePath()
{
    return Path.Combine(AppContext.BaseDirectory, "reminder-assets", "reminder-background.img");
}

static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
{
    var buffer = new byte[81920];
    long total = 0;

    while (true)
    {
        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read == 0)
        {
            return;
        }

        total += read;
        if (total > maxBytes)
        {
            throw new IOException($"Image exceeds the maximum allowed size of {maxBytes} bytes.");
        }

        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }
}

static bool IsSupportedImage(byte[] bytes)
{
    if (bytes.Length >= 8 &&
        bytes[0] == 0x89 &&
        bytes[1] == 0x50 &&
        bytes[2] == 0x4E &&
        bytes[3] == 0x47 &&
        bytes[4] == 0x0D &&
        bytes[5] == 0x0A &&
        bytes[6] == 0x1A &&
        bytes[7] == 0x0A)
    {
        return true;
    }

    if (bytes.Length >= 3 &&
        bytes[0] == 0xFF &&
        bytes[1] == 0xD8 &&
        bytes[2] == 0xFF)
    {
        return true;
    }

    if (bytes.Length >= 6 &&
        bytes[0] == 0x47 &&
        bytes[1] == 0x49 &&
        bytes[2] == 0x46 &&
        bytes[3] == 0x38 &&
        (bytes[4] == 0x37 || bytes[4] == 0x39) &&
        bytes[5] == 0x61)
    {
        return true;
    }

    return bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D;
}
