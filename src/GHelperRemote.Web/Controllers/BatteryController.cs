using System.Management;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/battery")]
public class BatteryController : ControllerBase
{
    private readonly AcpiSensorService _acpiService;
    private readonly GHelperConfigService _configService;
    private readonly ILogger<BatteryController> _logger;

    public BatteryController(
        AcpiSensorService acpiService,
        GHelperConfigService configService,
        ILogger<BatteryController> logger)
    {
        _acpiService = acpiService;
        _configService = configService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetBatteryStatus()
    {
        try
        {
            var config = await _configService.ReadConfigAsync();
            var status = ReadBatteryFromWmi(config);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read battery status");
            return StatusCode(500, new { error = "Failed to read battery status" });
        }
    }

    [HttpPut]
    public async Task<IActionResult> SetChargeLimit([FromBody] ChargeLimitRequest request)
    {
        if (request.ChargeLimit < 20 || request.ChargeLimit > 100)
            return BadRequest(new { error = "Charge limit must be between 20 and 100" });

        try
        {
            // Apply directly to hardware via ACPI
            if (!_acpiService.SetBatteryChargeLimit(request.ChargeLimit))
            {
                return StatusCode(500, new { error = "Failed to set battery charge limit via ACPI. Check logs for details." });
            }

            // Persist to config
            try
            {
                await _configService.WriteConfigAsync(new Dictionary<string, object>
                {
                    ["charge_limit"] = request.ChargeLimit
                });
            }
            catch (Exception configEx)
            {
                _logger.LogWarning(configEx, "Charge limit applied to hardware but config write failed");
            }

            var config = await _configService.ReadConfigAsync();
            var status = ReadBatteryFromWmi(config);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set charge limit");
            return StatusCode(500, new { error = "Failed to set charge limit" });
        }
    }

    private BatteryStatus ReadBatteryFromWmi(Dictionary<string, JsonElement> config)
    {
        var status = new BatteryStatus();

        try
        {
            using var batterySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            foreach (var obj in batterySearcher.Get())
            {
                status.ChargePercent = Convert.ToInt32(obj["EstimatedChargeRemaining"] ?? 0);
                var statusCode = Convert.ToInt32(obj["BatteryStatus"] ?? 0);
                status.IsCharging = statusCode == 2;
            }

            using var rateSearcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus");
            foreach (var obj in rateSearcher.Get())
            {
                status.ChargeRate = Convert.ToInt32(obj["ChargeRate"] ?? 0);
                status.DischargeRate = Convert.ToInt32(obj["DischargeRate"] ?? 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read battery status via WMI");
        }

        status.ChargeLimit = config.TryGetValue("charge_limit", out var clVal)
            ? clVal.GetInt32()
            : 100;

        return status;
    }
}

public class ChargeLimitRequest
{
    public int ChargeLimit { get; set; }
}
