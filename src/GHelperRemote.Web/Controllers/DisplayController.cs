using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/display")]
public class DisplayController : ControllerBase
{
    private readonly GHelperConfigService _configService;
    private readonly GHelperProcessService _processService;
    private readonly ILogger<DisplayController> _logger;

    public DisplayController(
        GHelperConfigService configService,
        GHelperProcessService processService,
        ILogger<DisplayController> logger)
    {
        _configService = configService;
        _processService = processService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetDisplaySettings()
    {
        try
        {
            var config = await _configService.ReadConfigAsync();

            return Ok(new
            {
                minRefreshRate = config.TryGetValue("min_rate", out var minVal) ? minVal.GetInt32() : 60,
                maxRefreshRate = config.TryGetValue("max_rate", out var maxVal) ? maxVal.GetInt32() : 165,
                screenAuto = config.TryGetValue("screen_auto", out var autoVal) && autoVal.ValueKind == JsonValueKind.True,
                overdrive = config.TryGetValue("overdrive", out var odVal) && odVal.ValueKind == JsonValueKind.True
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read display settings");
            return StatusCode(500, new { error = "Failed to read display settings" });
        }
    }

    [HttpPut]
    public async Task<IActionResult> SetDisplaySettings([FromBody] DisplaySettings settings)
    {
        if (settings.MinRefreshRate <= 0)
            return BadRequest(new { error = "Minimum refresh rate must be greater than 0" });

        if (settings.MaxRefreshRate <= 0)
            return BadRequest(new { error = "Maximum refresh rate must be greater than 0" });

        if (settings.MinRefreshRate > settings.MaxRefreshRate)
            return BadRequest(new { error = "Minimum refresh rate cannot exceed maximum refresh rate" });

        try
        {
            await _configService.WriteConfigAsync(new Dictionary<string, object>
            {
                ["min_rate"] = settings.MinRefreshRate,
                ["max_rate"] = settings.MaxRefreshRate,
                ["screen_auto"] = settings.ScreenAuto,
                ["overdrive"] = settings.Overdrive
            });

            try
            {
                await _processService.RestartGHelperAsync();
            }
            catch (FileNotFoundException)
            {
                return StatusCode(500, new
                {
                    code = "ghelper_exe_not_found",
                    error = "G-Helper executable path is not configured. Set the full path to GHelper.exe in settings or use auto-detect."
                });
            }
            catch (Exception restartEx)
            {
                _logger.LogWarning(restartEx, "Display settings saved but G-Helper restart failed");
            }

            return Ok(new
            {
                minRefreshRate = settings.MinRefreshRate,
                maxRefreshRate = settings.MaxRefreshRate,
                screenAuto = settings.ScreenAuto,
                overdrive = settings.Overdrive
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set display settings");
            return StatusCode(500, new { error = "Failed to set display settings" });
        }
    }
}
