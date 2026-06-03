using System.Net;
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

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _configurationResolver = new AgentConfigurationResolver(logger);
        _agentId = AgentIdentityStore.GetOrCreateAgentId();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryIndex = 0;
        var forceRefresh = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            AgentRuntimeState runtimeState;

            try
            {
                runtimeState = await _configurationResolver.ResolveAsync(forceRefresh, stoppingToken);
                forceRefresh = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve agent bootstrap configuration.");
                forceRefresh = true;
                await DelayBeforeRetryAsync(retryIndex++, stoppingToken);
                continue;
            }

            using var socket = new ClientWebSocket();

            try
            {
                await socket.ConnectAsync(new Uri(runtimeState.ServerUrl), stoppingToken);
                _logger.LogInformation("Connected to {ServerUrl}", runtimeState.ServerUrl);
                retryIndex = 0;

                var registration = BuildRegistrationMessage();
                await SendAsync(socket, AgentMessageTypes.Register, registration, stoppingToken);

                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var heartbeat = BuildHeartbeatMessage();
                    await SendAsync(socket, AgentMessageTypes.Heartbeat, heartbeat, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(runtimeState.HeartbeatSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent loop failed. Refreshing bootstrap configuration and retrying.");
                forceRefresh = true;
                await DelayBeforeRetryAsync(retryIndex++, stoppingToken);
            }
        }
    }

    private AgentRegisterMessage BuildRegistrationMessage()
    {
        var snapshot = DeviceSnapshot.Capture();
        return new AgentRegisterMessage(
            _agentId,
            snapshot.HostName,
            snapshot.IpAddress,
            snapshot.MacAddress,
            snapshot.CurrentUser,
            snapshot.OsVersion,
            snapshot.AgentVersion,
            DateTimeOffset.UtcNow);
    }

    private AgentHeartbeatMessage BuildHeartbeatMessage()
    {
        var snapshot = DeviceSnapshot.Capture();
        return new AgentHeartbeatMessage(
            _agentId,
            snapshot.IpAddress,
            snapshot.CurrentUser,
            DateTimeOffset.UtcNow);
    }

    private static async Task SendAsync<T>(ClientWebSocket socket, string type, T payload, CancellationToken cancellationToken)
    {
        var envelope = new { type, payload };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static Task DelayBeforeRetryAsync(int retryIndex, CancellationToken cancellationToken)
    {
        var boundedIndex = Math.Min(retryIndex, RetryDelays.Length - 1);
        return Task.Delay(RetryDelays[boundedIndex], cancellationToken);
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

    public async Task<AgentRuntimeState> ResolveAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var persisted = AgentRuntimeStateStore.Load();
        if (!forceRefresh && persisted is not null)
        {
            return persisted;
        }

        foreach (var candidateBaseUrl in GetBootstrapCandidates(persisted))
        {
            var resolved = await TryFetchBootstrapAsync(candidateBaseUrl, cancellationToken);
            if (resolved is not null)
            {
                AgentRuntimeStateStore.Save(resolved);
                return resolved;
            }
        }

        var discoveredBaseUrls = await DiscoverServerBaseUrlsAsync(cancellationToken);
        foreach (var candidateBaseUrl in discoveredBaseUrls)
        {
            var resolved = await TryFetchBootstrapAsync(candidateBaseUrl, cancellationToken);
            if (resolved is not null)
            {
                AgentRuntimeStateStore.Save(resolved);
                return resolved;
            }
        }

        if (persisted is not null)
        {
            _logger.LogWarning("Falling back to cached bootstrap configuration at {ServerBaseUrl}", persisted.ServerBaseUrl);
            return persisted;
        }

        throw new InvalidOperationException("No LanAdmin server bootstrap endpoint could be resolved.");
    }

    private IEnumerable<string> GetBootstrapCandidates(AgentRuntimeState? persisted)
    {
        if (!string.IsNullOrWhiteSpace(persisted?.ServerBaseUrl))
        {
            yield return persisted.ServerBaseUrl;
        }

        foreach (var baseUrl in _defaults.ServerBaseUrls)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                yield return baseUrl;
            }
        }
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
                response.HeartbeatSeconds > 0 ? response.HeartbeatSeconds : _defaults.HeartbeatSeconds,
                DateTimeOffset.UtcNow);
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

    private async Task<IReadOnlyList<string>> DiscoverServerBaseUrlsAsync(CancellationToken cancellationToken)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new AgentDiscoveryRequest(BootstrapProtocol.DiscoveryRequestType, BootstrapProtocol.Version),
            AgentJson.Options);

        for (var attempt = 0; attempt < _defaults.DiscoveryRetryCount; attempt++)
        {
            using var udpClient = new UdpClient(AddressFamily.InterNetwork);
            udpClient.EnableBroadcast = true;
            await udpClient.SendAsync(
                requestBytes,
                requestBytes.Length,
                new IPEndPoint(IPAddress.Broadcast, _defaults.DiscoveryUdpPort));

            var deadline = DateTime.UtcNow.AddMilliseconds(_defaults.DiscoveryTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                try
                {
                    var receiveTask = udpClient.ReceiveAsync(cancellationToken).AsTask();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(remaining, cancellationToken));
                    if (completedTask != receiveTask)
                    {
                        break;
                    }

                    var result = await receiveTask;
                    var response = JsonSerializer.Deserialize<AgentDiscoveryResponse>(result.Buffer, AgentJson.Options);
                    if (response is null ||
                        response.Version != BootstrapProtocol.Version ||
                        !string.Equals(response.Type, BootstrapProtocol.DiscoveryResponseType, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(response.ServerBaseUrl))
                    {
                        continue;
                    }

                    discovered.Add(response.ServerBaseUrl.TrimEnd('/'));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or JsonException)
                {
                    _logger.LogDebug(ex, "Ignoring invalid UDP discovery response.");
                }
            }

            if (discovered.Count > 0)
            {
                break;
            }
        }

        return discovered.ToArray();
    }
}

internal sealed record AgentRuntimeState(
    string ServerBaseUrl,
    string ServerUrl,
    int HeartbeatSeconds,
    DateTimeOffset LastUpdatedAt);

internal sealed class AgentBootstrapDefaults
{
    public int HeartbeatSeconds { get; init; } = 30;
    public string[] ServerBaseUrls { get; init; } =
    {
        "http://lanadmin-server:5000",
        "http://server:5000",
        "http://localhost:5000"
    };

    public string EndpointPath { get; init; } = "/api/bootstrap/agent";
    public int DiscoveryUdpPort { get; init; } = 5010;
    public int DiscoveryTimeoutMilliseconds { get; init; } = 3000;
    public int DiscoveryRetryCount { get; init; } = 3;

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
            ServerBaseUrls = bootstrapNode.TryGetProperty("ServerBaseUrls", out var baseUrlsNode) &&
                             baseUrlsNode.ValueKind == JsonValueKind.Array
                ? baseUrlsNode.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : new AgentBootstrapDefaults().ServerBaseUrls,
            EndpointPath = bootstrapNode.TryGetProperty("EndpointPath", out var endpointPathNode)
                ? endpointPathNode.GetString() ?? "/api/bootstrap/agent"
                : "/api/bootstrap/agent",
            DiscoveryUdpPort = bootstrapNode.TryGetProperty("DiscoveryUdpPort", out var discoveryPortNode) &&
                               discoveryPortNode.TryGetInt32(out var discoveryPort) &&
                               discoveryPort > 0
                ? discoveryPort
                : 5010,
            DiscoveryTimeoutMilliseconds = bootstrapNode.TryGetProperty("DiscoveryTimeoutMilliseconds", out var discoveryTimeoutNode) &&
                                           discoveryTimeoutNode.TryGetInt32(out var discoveryTimeout) &&
                                           discoveryTimeout > 0
                ? discoveryTimeout
                : 3000,
            DiscoveryRetryCount = bootstrapNode.TryGetProperty("DiscoveryRetryCount", out var retryCountNode) &&
                                  retryCountNode.TryGetInt32(out var retryCount) &&
                                  retryCount > 0
                ? retryCount
                : 3
        };
    }
}

internal static class AgentRuntimeStateStore
{
    private static readonly string RuntimeStatePath = Path.Combine(AgentStoragePaths.AgentDirectory, "runtime.json");

    public static AgentRuntimeState? Load()
    {
        if (!File.Exists(RuntimeStatePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(RuntimeStatePath);
            return JsonSerializer.Deserialize<AgentRuntimeState>(stream, AgentJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AgentRuntimeState state)
    {
        Directory.CreateDirectory(AgentStoragePaths.AgentDirectory);

        var json = JsonSerializer.Serialize(state, AgentJson.Options);
        File.WriteAllText(RuntimeStatePath, json);
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
    string AgentVersion)
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
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
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
