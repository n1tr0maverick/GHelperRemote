using GHelperRemote.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Background service that polls ACPI sensor data every second, enriches it with
/// performance mode and GPU mode information from the G-Helper config, and broadcasts
/// the result via <see cref="ISensorBroadcaster"/> for consumption by connected clients.
/// </summary>
public sealed class SensorPollingService : BackgroundService
{
    private readonly AcpiSensorService _sensorService;
    private readonly GHelperConfigService _configService;
    private readonly ISensorBroadcaster _broadcaster;
    private readonly ILogger<SensorPollingService> _logger;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);

    public SensorPollingService(
        AcpiSensorService sensorService,
        GHelperConfigService configService,
        ISensorBroadcaster broadcaster,
        ILogger<SensorPollingService> logger)
    {
        _sensorService = sensorService;
        _configService = configService;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Sensor polling service started (interval: {Interval}s)", PollingInterval.TotalSeconds);

        // Use a PeriodicTimer for efficient, drift-free polling
        using var timer = new PeriodicTimer(PollingInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Read hardware sensor values from ACPI
                var status = _sensorService.ReadSensors();

                // Enrich with performance mode and GPU mode from G-Helper config
                await EnrichWithConfigDataAsync(status);

                // Broadcast to connected clients
                await _broadcaster.BroadcastSensorDataAsync(status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown - exit the loop
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during sensor polling cycle");
                // Continue polling - don't crash the service on transient errors
            }
        }

        _logger.LogInformation("Sensor polling service stopped");
    }

    private async Task EnrichWithConfigDataAsync(SystemStatus status)
    {
        try
        {
            var performanceMode = await _configService.GetValueAsync<int>("performance_mode");
            status.PerformanceMode = performanceMode;
            status.PerformanceModeName = AcpiSensorService.GetPerformanceModeName(performanceMode);

            var gpuMode = await _configService.GetValueAsync<int>("gpu_mode");
            status.GpuMode = gpuMode;
            status.GpuModeName = AcpiSensorService.GetGpuModeName(gpuMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read mode data from config, using defaults");
            status.PerformanceModeName = "Unknown";
            status.GpuModeName = "Unknown";
        }
    }
}
