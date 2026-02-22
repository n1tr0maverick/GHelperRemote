using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/mode")]
public class PerformanceModeController : ControllerBase
{
    private static readonly string[] ModeNames = { "Balanced", "Turbo", "Silent" };

    private readonly GHelperConfigService _configService;
    private readonly GHelperProcessService _processService;
    private readonly ILogger<PerformanceModeController> _logger;

    public PerformanceModeController(
        GHelperConfigService configService,
        GHelperProcessService processService,
        ILogger<PerformanceModeController> logger)
    {
        _configService = configService;
        _processService = processService;
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
            await _configService.WriteConfigAsync(new Dictionary<string, object>
            {
                ["performance_mode"] = request.Mode
            });

            await _processService.RestartGHelperAsync();

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
