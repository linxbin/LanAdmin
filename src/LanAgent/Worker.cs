using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LanAdmin.Contracts;

namespace LanAgent;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AgentRuntimeOptions _options;
    private readonly string _agentId;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _options = AgentRuntimeOptions.Load();
        _agentId = AgentIdentityStore.GetOrCreateAgentId();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();

            try
            {
                await socket.ConnectAsync(new Uri(_options.ServerUrl), stoppingToken);
                _logger.LogInformation("Connected to {ServerUrl}", _options.ServerUrl);

                var registration = BuildRegistrationMessage();
                await SendAsync(socket, AgentMessageTypes.Register, registration, stoppingToken);

                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var heartbeat = BuildHeartbeatMessage();
                    await SendAsync(socket, AgentMessageTypes.Heartbeat, heartbeat, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent loop failed. Reconnecting shortly.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
}

internal sealed class AgentRuntimeOptions
{
    public string ServerUrl { get; init; } = "ws://localhost:5000/ws/agent";
    public int HeartbeatSeconds { get; init; } = 30;

    public static AgentRuntimeOptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AgentRuntimeOptions();
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("Agent", out var agentNode))
        {
            return new AgentRuntimeOptions();
        }

        return new AgentRuntimeOptions
        {
            ServerUrl = agentNode.TryGetProperty("ServerUrl", out var serverUrl) ? serverUrl.GetString() ?? "ws://localhost:5000/ws/agent" : "ws://localhost:5000/ws/agent",
            HeartbeatSeconds = agentNode.TryGetProperty("HeartbeatSeconds", out var heartbeat) ? heartbeat.GetInt32() : 30
        };
    }
}

internal static class AgentIdentityStore
{
    private static readonly string AgentDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanAgent");

    private static readonly string AgentIdPath = Path.Combine(AgentDirectory, "agent-id.txt");

    public static string GetOrCreateAgentId()
    {
        Directory.CreateDirectory(AgentDirectory);

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
        return new DeviceSnapshot(
            Environment.MachineName,
            GetPrimaryIpAddress(),
            GetPrimaryMacAddress(),
            Environment.UserName,
            Environment.OSVersion.VersionString,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");
    }

    private static string GetPrimaryIpAddress()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        foreach (var nic in interfaces)
        {
            var properties = nic.GetIPProperties();
            var address = properties.UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !x.ToString().StartsWith("169.254."));

            if (address is not null)
            {
                return address.ToString();
            }
        }

        return "0.0.0.0";
    }

    private static string GetPrimaryMacAddress()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        return nic?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
    }
}
