using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LanAdmin.Contracts;

namespace LanAgent;

public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan ServerConfigurationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    private readonly ILogger<Worker> _logger;
    private readonly AgentConfigurationResolver _configurationResolver;
    private readonly string _agentId;
    private int _shutdownThresholdDays;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _configurationResolver = new AgentConfigurationResolver(logger);
        _agentId = AgentIdentityStore.GetOrCreateAgentId();
        _shutdownThresholdDays = AgentNotifierStateStore.Load()?.ShutdownThresholdDays ?? ShutdownThresholdDefaults.DefaultDays;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var currentSnapshot = DeviceSnapshot.Capture();
            UpdateNotifierState(currentSnapshot);
            NotifierProcessManager.EnsureRunning(_logger);

            AgentRuntimeState runtimeState;

            try
            {
                runtimeState = await _configurationResolver.ResolveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve agent bootstrap configuration.");
                await DelayBeforeRetryAsync(retryIndex++, stoppingToken);
                continue;
            }

            using var socket = new ClientWebSocket();

            try
            {
                await socket.ConnectAsync(new Uri(runtimeState.ServerUrl), stoppingToken);
                _logger.LogInformation("Connected to {ServerUrl}", runtimeState.ServerUrl);
                retryIndex = 0;

                var receiveTask = ProcessServerMessagesAsync(socket, runtimeState.ServerBaseUrl, stoppingToken);

                var registrationSnapshot = DeviceSnapshot.Capture();
                UpdateNotifierState(registrationSnapshot);
                NotifierProcessManager.EnsureRunning(_logger);
                var registration = BuildRegistrationMessage(registrationSnapshot);
                await SendAsync(socket, AgentMessageTypes.Register, registration, stoppingToken);

                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested && !receiveTask.IsCompleted)
                {
                    var heartbeatSnapshot = DeviceSnapshot.Capture();
                    UpdateNotifierState(heartbeatSnapshot);
                    NotifierProcessManager.EnsureRunning(_logger);
                    var heartbeat = BuildHeartbeatMessage(heartbeatSnapshot);
                    await SendAsync(socket, AgentMessageTypes.Heartbeat, heartbeat, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(runtimeState.HeartbeatSeconds), stoppingToken);
                }

                await receiveTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent loop failed. Retrying bootstrap using the configured server address.");
                await DelayBeforeRetryAsync(retryIndex++, stoppingToken);
            }
        }
    }

    private AgentRegisterMessage BuildRegistrationMessage(DeviceSnapshot snapshot)
    {
        return new AgentRegisterMessage(
            _agentId,
            snapshot.HostName,
            snapshot.IpAddress,
            snapshot.MacAddress,
            snapshot.CurrentUser,
            snapshot.OsVersion,
            snapshot.AgentVersion,
            snapshot.UptimeSeconds,
            DateTimeOffset.UtcNow);
    }

    private AgentHeartbeatMessage BuildHeartbeatMessage(DeviceSnapshot snapshot)
    {
        return new AgentHeartbeatMessage(
            _agentId,
            snapshot.IpAddress,
            snapshot.CurrentUser,
            snapshot.UptimeSeconds,
            DateTimeOffset.UtcNow);
    }

    private async Task ProcessServerMessagesAsync(ClientWebSocket socket, string serverBaseUrl, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await ReceiveMessageAsync(socket, cancellationToken);
            if (payload is null)
            {
                return;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<AgentEnvelope>(payload, AgentJson.Options);
                if (envelope is null)
                {
                    continue;
                }

                switch (envelope.Type)
                {
                    case ServerMessageTypes.Configuration:
                    {
                        var configuration = envelope.Payload.Deserialize<AgentConfigurationMessage>(AgentJson.Options);
                        if (configuration is null ||
                            !string.Equals(configuration.AgentId, _agentId, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        _shutdownThresholdDays = configuration.ShutdownThresholdDays;
                        if (configuration.ReminderStyle is not null)
                        {
                            await UpdateReminderStyleAsync(configuration.ReminderStyle, serverBaseUrl, cancellationToken);
                        }

                        UpdateNotifierState(DeviceSnapshot.Capture());
                        break;
                    }
                    case ServerMessageTypes.ManualShutdownReminder:
                    {
                        var reminder = envelope.Payload.Deserialize<ManualShutdownReminderMessage>(AgentJson.Options);
                        if (reminder is null ||
                            !string.Equals(reminder.AgentId, _agentId, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        TriggerManualShutdownReminder(reminder);
                        break;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid server payload received.");
            }
        }
    }

    private static async Task SendAsync<T>(ClientWebSocket socket, string type, T payload, CancellationToken cancellationToken)
    {
        var envelope = new { type, payload };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static Task DelayBeforeRetryAsync(int retryIndex, CancellationToken cancellationToken)
    {
        var boundedIndex = Math.Min(retryIndex, RetryDelays.Length - 1);
        return Task.Delay(RetryDelays[boundedIndex], cancellationToken);
    }

    private void UpdateNotifierState(DeviceSnapshot snapshot)
    {
        AgentNotifierStateStore.Save(new AgentNotifierState(
            _agentId,
            snapshot.HostName,
            snapshot.CurrentUser,
            snapshot.UptimeSeconds,
            _shutdownThresholdDays,
            DateTimeOffset.UtcNow));
    }

    private async Task UpdateReminderStyleAsync(ReminderStyleDto style, string serverBaseUrl, CancellationToken cancellationToken)
    {
        var backgroundImagePath = await AgentReminderBackgroundImageCache.RefreshAsync(
            style,
            serverBaseUrl,
            _logger,
            cancellationToken);
        AgentReminderStyleStore.Save(style, backgroundImagePath);
    }

    private void TriggerManualShutdownReminder(ManualShutdownReminderMessage reminder)
    {
        UpdateNotifierState(DeviceSnapshot.Capture());
        AgentManualReminderRequestStore.Save(new AgentManualReminderRequest(reminder.CommandId, reminder.RequestedAt));
        NotifierProcessManager.EnsureRunning(_logger);
        AgentManualReminderSignal.Notify();
    }
}

internal sealed class AgentConfigurationResolver
{
    private readonly ILogger _logger;
    private readonly AgentBootstrapDefaults _defaults;
    private readonly HttpClient _httpClient;

    public AgentConfigurationResolver(ILogger logger)
    {
        _logger = logger;
        _defaults = AgentBootstrapDefaults.Load();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task<AgentRuntimeState> ResolveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_defaults.ServerBaseUrl))
        {
            throw new InvalidOperationException("Bootstrap:ServerBaseUrl is not configured.");
        }

        var resolved = await TryFetchBootstrapAsync(_defaults.ServerBaseUrl, cancellationToken);
        if (resolved is not null)
        {
            return resolved;
        }

        throw new InvalidOperationException($"Bootstrap request failed for configured ServerBaseUrl '{_defaults.ServerBaseUrl}'.");
    }

    private async Task<AgentRuntimeState?> TryFetchBootstrapAsync(string serverBaseUrl, CancellationToken cancellationToken)
    {
        var requestUri = BuildBootstrapUri(serverBaseUrl);

        try
        {
            var response = await _httpClient.GetFromJsonAsync<AgentBootstrapResponse>(requestUri, AgentJson.Options, cancellationToken);
            if (response is null || string.IsNullOrWhiteSpace(response.ServerBaseUrl) || string.IsNullOrWhiteSpace(response.AgentWebSocketUrl))
            {
                return null;
            }

            return new AgentRuntimeState(
                response.ServerBaseUrl.TrimEnd('/'),
                response.AgentWebSocketUrl,
                response.HeartbeatSeconds > 0 ? response.HeartbeatSeconds : _defaults.HeartbeatSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Bootstrap request failed for {ServerBaseUrl}", serverBaseUrl);
            return null;
        }
    }

    private Uri BuildBootstrapUri(string serverBaseUrl)
    {
        return new Uri(new Uri(serverBaseUrl.TrimEnd('/') + "/"), _defaults.EndpointPath.TrimStart('/'));
    }
}

internal sealed record AgentRuntimeState(
    string ServerBaseUrl,
    string ServerUrl,
    int HeartbeatSeconds);

internal sealed class AgentBootstrapDefaults
{
    public int HeartbeatSeconds { get; init; } = 30;
    public string ServerBaseUrl { get; init; } = "http://127.0.0.1:5000";
    public string EndpointPath { get; init; } = "/api/bootstrap/agent";

    public static AgentBootstrapDefaults Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AgentBootstrapDefaults();
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var heartbeatSeconds = 30;
        if (document.RootElement.TryGetProperty("Agent", out var agentNode) &&
            agentNode.TryGetProperty("HeartbeatSeconds", out var heartbeatNode) &&
            heartbeatNode.TryGetInt32(out var heartbeatValue) &&
            heartbeatValue > 0)
        {
            heartbeatSeconds = heartbeatValue;
        }

        if (!document.RootElement.TryGetProperty("Bootstrap", out var bootstrapNode))
        {
            return new AgentBootstrapDefaults
            {
                HeartbeatSeconds = heartbeatSeconds
            };
        }

        return new AgentBootstrapDefaults
        {
            HeartbeatSeconds = heartbeatSeconds,
            ServerBaseUrl = bootstrapNode.TryGetProperty("ServerBaseUrl", out var serverBaseUrlNode)
                ? serverBaseUrlNode.GetString() ?? "http://127.0.0.1:5000"
                : "http://127.0.0.1:5000",
            EndpointPath = bootstrapNode.TryGetProperty("EndpointPath", out var endpointPathNode)
                ? endpointPathNode.GetString() ?? "/api/bootstrap/agent"
                : "/api/bootstrap/agent"
        };
    }
}

internal static class AgentIdentityStore
{
    private static readonly string AgentIdPath = Path.Combine(AgentStoragePaths.AgentDirectory, "agent-id.txt");

    public static string GetOrCreateAgentId()
    {
        Directory.CreateDirectory(AgentStoragePaths.AgentDirectory);

        if (File.Exists(AgentIdPath))
        {
            var value = File.ReadAllText(AgentIdPath).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        var agentId = Guid.NewGuid().ToString("N");
        File.WriteAllText(AgentIdPath, agentId);
        return agentId;
    }
}

internal static class AgentStoragePaths
{
    public static readonly string AgentDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanAdmin",
        "Agent");
}

internal static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

internal sealed record DeviceSnapshot(
    string HostName,
    string IpAddress,
    string MacAddress,
    string CurrentUser,
    string OsVersion,
    string AgentVersion,
    long UptimeSeconds)
{
    public static DeviceSnapshot Capture()
    {
        var networkIdentity = GetPrimaryNetworkIdentity();
        return new DeviceSnapshot(
            Environment.MachineName,
            networkIdentity.IpAddress,
            networkIdentity.MacAddress,
            Environment.UserName,
            Environment.OSVersion.VersionString,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds);
    }

    private static NetworkIdentity GetPrimaryNetworkIdentity()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x =>
                x.OperationalStatus == OperationalStatus.Up &&
                x.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                x.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

        foreach (var nic in interfaces)
        {
            var properties = nic.GetIPProperties();
            var address = properties.UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !x.ToString().StartsWith("169.254."));

            var macAddress = nic.GetPhysicalAddress().ToString();
            if (address is not null && !string.IsNullOrWhiteSpace(macAddress))
            {
                return new NetworkIdentity(address.ToString(), macAddress);
            }
        }

        foreach (var nic in interfaces)
        {
            var macAddress = nic.GetPhysicalAddress().ToString();
            if (!string.IsNullOrWhiteSpace(macAddress))
            {
                return new NetworkIdentity("0.0.0.0", macAddress);
            }
        }

        return new NetworkIdentity("0.0.0.0", "UNKNOWN");
    }

    private sealed record NetworkIdentity(string IpAddress, string MacAddress);
}
