using LanAdmin.Server.Data;
using Microsoft.Extensions.Options;

namespace LanAdmin.Server.Services;

public sealed class OfflineDeviceMonitor : BackgroundService
{
    private readonly IDeviceRepository _repository;
    private readonly AgentOptions _options;
    private readonly ILogger<OfflineDeviceMonitor> _logger;

    public OfflineDeviceMonitor(
        IDeviceRepository repository,
        IOptions<AgentOptions> options,
        ILogger<OfflineDeviceMonitor> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var threshold = DateTimeOffset.UtcNow.AddSeconds(-_options.OfflineThresholdSeconds);
                var marked = await _repository.MarkOfflineDevicesAsync(threshold, stoppingToken);
                if (marked > 0)
                {
                    _logger.LogInformation("Marked {Count} devices offline.", marked);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate offline devices.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
