using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Acpi;
using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/gpu")]
public class GpuModeController : ControllerBase
{
    private static readonly string[] ModeNames = { "Eco", "Standard", "Ultimate" };

    private readonly AcpiSensorService _acpiService;
    private readonly GHelperConfigService _configService;
    private readonly ILogger<GpuModeController> _logger;

    public GpuModeController(
        AcpiSensorService acpiService,
        GHelperConfigService configService,
        ILogger<GpuModeController> logger)
    {
        _acpiService = acpiService;
        _configService = configService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetGpuMode()
    {
        try
        {
            // Read actual hardware state via ACPI
            var mode = _acpiService.GetGpuMode();

            // Also read auto setting from config
            var config = await _configService.ReadConfigAsync();
            var auto = config.TryGetValue("gpu_auto", out var aVal)
                && aVal.ValueKind == System.Text.Json.JsonValueKind.True;

            return Ok(new
            {
                mode,
                name = ModeNames.ElementAtOrDefault(mode) ?? "Unknown",
                auto
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read GPU mode");
            return StatusCode(500, new { error = "Failed to read GPU mode" });
        }
    }

    [HttpPut]
    public async Task<IActionResult> SetGpuMode([FromBody] GpuModeRequest request)
    {
        if (request.Mode < 0 || request.Mode > 2)
            return BadRequest(new { error = "GPU mode must be between 0 and 2" });

        try
        {
            bool success;
            string? warning = null;

            switch (request.Mode)
            {
                case AcpiConstants.GpuModeEco:
                    // Enable Eco mode (turn off discrete GPU)
                    success = _acpiService.SetGpuEco(1);
                    break;

                case AcpiConstants.GpuModeStandard:
                    // Disable Eco mode (turn on discrete GPU)
                    success = _acpiService.SetGpuEco(0);
                    break;

                case AcpiConstants.GpuModeUltimate:
                    // Set MUX to direct GPU mode (requires reboot)
                    success = _acpiService.SetGpuMux(0);
                    warning = "Ultimate (MUX switch) mode requires a system reboot to take effect.";
                    break;

                default:
                    return BadRequest(new { error = "Invalid GPU mode" });
            }

            if (!success)
            {
                return StatusCode(500, new { error = "Failed to set GPU mode via ACPI. Check logs for details." });
            }

            // Persist to config for GHelper UI sync
            try
            {
                await _configService.WriteConfigAsync(new Dictionary<string, object>
                {
                    ["gpu_mode"] = request.Mode,
                    ["gpu_auto"] = request.Auto
                });
            }
            catch (Exception configEx)
            {
                _logger.LogWarning(configEx, "GPU mode applied to hardware but config write failed");
            }

            return Ok(new
            {
                mode = request.Mode,
                name = ModeNames[request.Mode],
                auto = request.Auto,
                warning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GPU mode");
            return StatusCode(500, new { error = "Failed to set GPU mode" });
        }
    }
}
