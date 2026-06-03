using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanAdmin.Contracts;
using Microsoft.Extensions.Options;

namespace LanAdmin.Server.Services;

public sealed class BootstrapDiscoveryService : BackgroundService
{
    private readonly ILogger<BootstrapDiscoveryService> _logger;
    private readonly BootstrapOptions _bootstrapOptions;

    public BootstrapDiscoveryService(
        ILogger<BootstrapDiscoveryService> logger,
        IOptions<BootstrapOptions> bootstrapOptions)
    {
        _logger = logger;
        _bootstrapOptions = bootstrapOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udpClient = new UdpClient(AddressFamily.InterNetwork);
        udpClient.EnableBroadcast = true;
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _bootstrapOptions.DiscoveryUdpPort));

        _logger.LogInformation(
            "Agent discovery responder listening on UDP {Port} with server base URL {ServerBaseUrl}",
            _bootstrapOptions.DiscoveryUdpPort,
            _bootstrapOptions.ServerBaseUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                var request = JsonSerializer.Deserialize<AgentDiscoveryRequest>(result.Buffer, JsonDefaults.Options);
                if (request is null ||
                    request.Version != BootstrapProtocol.Version ||
                    !string.Equals(request.Type, BootstrapProtocol.DiscoveryRequestType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var response = new AgentDiscoveryResponse(
                    BootstrapProtocol.DiscoveryResponseType,
                    BootstrapProtocol.Version,
                    _bootstrapOptions.ServerBaseUrl);

                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options);
                await udpClient.SendAsync(responseBytes, result.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent discovery responder failed to process a UDP packet.");
            }
        }
    }
}
