using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/mode")]
public class PerformanceModeController : ControllerBase
{
    private static readonly string[] ModeNames = { "Balanced", "Turbo", "Silent" };

    private readonly AcpiSensorService _acpiService;
    private readonly GHelperConfigService _configService;
    private readonly ILogger<PerformanceModeController> _logger;

    public PerformanceModeController(
        AcpiSensorService acpiService,
        GHelperConfigService configService,
        ILogger<PerformanceModeController> logger)
    {
        _acpiService = acpiService;
        _configService = configService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetMode()
    {
        try
        {
            var config = await _configService.ReadConfigAsync();
            var mode = config.TryGetValue("performance_mode", out var val)
                ? val.GetInt32()
                : 0;

            return Ok(new
            {
                mode,
                name = mode >= 0 && mode < ModeNames.Length ? ModeNames[mode] : "Unknown"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read performance mode");
            return StatusCode(500, new { error = "Failed to read performance mode" });
        }
    }

    [HttpPut]
    public async Task<IActionResult> SetMode([FromBody] PerformanceModeRequest request)
    {
        if (request.Mode < 0 || request.Mode > 2)
            return BadRequest(new { error = "Mode must be between 0 and 2" });

        try
        {
            // Apply directly to hardware via ACPI DEVS call
            if (!_acpiService.SetPerformanceMode(request.Mode))
            {
                return StatusCode(500, new { error = "Failed to set performance mode via ACPI. Check logs for details." });
            }

            // Persist to config for GHelper UI sync
            try
            {
                await _configService.WriteConfigAsync(new Dictionary<string, object>
                {
                    ["performance_mode"] = request.Mode
                });
            }
            catch (Exception configEx)
            {
                _logger.LogWarning(configEx, "Mode applied to hardware but config write failed");
            }

            return Ok(new
            {
                mode = request.Mode,
                name = ModeNames[request.Mode]
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set performance mode");
            return StatusCode(500, new { error = "Failed to set performance mode" });
        }
    }
}
