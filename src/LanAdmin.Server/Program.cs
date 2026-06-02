using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LanAdmin.Contracts;
using LanAdmin.Server;
using LanAdmin.Server.Data;
using LanAdmin.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
builder.Services.AddHostedService<OfflineDeviceMonitor>();

var app = builder.Build();

app.UseWebSockets();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
    await repository.InitializeAsync();
}

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

    var group = await repository.CreateGroupAsync(request.Name.Trim(), cancellationToken);
    return Results.Ok(group);
});

app.MapPut("/api/groups/{groupId:long}", async (long groupId, RenameGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("Group name is required.");
    }

    var group = await repository.RenameGroupAsync(groupId, request.Name.Trim(), cancellationToken);
    return group is null ? Results.NotFound() : Results.Ok(group);
});

app.MapPost("/api/devices/{agentId}/assign-group", async (string agentId, AssignGroupRequest request, IDeviceRepository repository, CancellationToken cancellationToken) =>
{
    var updated = await repository.AssignGroupAsync(agentId, request.GroupId, cancellationToken);
    return updated ? Results.Ok() : Results.NotFound();
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
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentSocket");

    var buffer = new byte[16 * 1024];

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
                        await repository.UpsertRegistrationAsync(message, context.RequestAborted);
                    }

                    break;
                }
                case AgentMessageTypes.Heartbeat:
                {
                    var message = envelope.Payload.Deserialize<AgentHeartbeatMessage>(JsonDefaults.Options);
                    if (message is not null)
                    {
                        await repository.RecordHeartbeatAsync(message, context.RequestAborted);
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
});

await app.RunAsync();

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
