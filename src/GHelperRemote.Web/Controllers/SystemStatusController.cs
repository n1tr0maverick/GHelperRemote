using System.Management;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/status")]
public class SystemStatusController : ControllerBase
{
    private readonly AcpiSensorService _sensorService;
    private readonly GHelperConfigService _configService;
    private readonly ILogger<SystemStatusController> _logger;

    public SystemStatusController(
        AcpiSensorService sensorService,
        GHelperConfigService configService,
        ILogger<SystemStatusController> logger)
    {
        _sensorService = sensorService;
        _configService = configService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var status = _sensorService.ReadSensors();

            var config = await _configService.ReadConfigAsync();

            var performanceMode = config.TryGetValue("performance_mode", out var pmVal)
                ? pmVal.GetInt32()
                : 0;

            var gpuMode = config.TryGetValue("gpu_mode", out var gmVal)
                ? gmVal.GetInt32()
                : 0;

            status.PerformanceMode = performanceMode;
            status.PerformanceModeName = AcpiSensorService.GetPerformanceModeName(performanceMode);
            status.GpuMode = gpuMode;
            status.GpuModeName = AcpiSensorService.GetGpuModeName(gpuMode);
            status.Battery = ReadBatteryStatus(config);

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read system status");
            return StatusCode(500, new { error = "Failed to read system status" });
        }
    }

    private BatteryStatus ReadBatteryStatus(Dictionary<string, JsonElement> config)
    {
        var batteryStatus = new BatteryStatus();

        try
        {
            using var batterySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            foreach (var obj in batterySearcher.Get())
            {
                batteryStatus.ChargePercent = Convert.ToInt32(obj["EstimatedChargeRemaining"] ?? 0);
                var statusCode = Convert.ToInt32(obj["BatteryStatus"] ?? 0);
                batteryStatus.IsCharging = statusCode == 2;
            }

            using var rateSearcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus");
            foreach (var obj in rateSearcher.Get())
            {
                batteryStatus.ChargeRate = Convert.ToInt32(obj["ChargeRate"] ?? 0);
                batteryStatus.DischargeRate = Convert.ToInt32(obj["DischargeRate"] ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read battery status via WMI");
        }

        batteryStatus.ChargeLimit = config.TryGetValue("charge_limit", out var clVal)
            ? clVal.GetInt32()
            : 100;

        return batteryStatus;
    }
}
