using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/gpu")]
public class GpuModeController : ControllerBase
{
    private static readonly string[] ModeNames = { "Eco", "Standard", "Ultimate" };

    private readonly GHelperConfigService _configService;
    private readonly GHelperProcessService _processService;
    private readonly ILogger<GpuModeController> _logger;

    public GpuModeController(
        GHelperConfigService configService,
        GHelperProcessService processService,
        ILogger<GpuModeController> logger)
    {
        _configService = configService;
        _processService = processService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetGpuMode()
    {
        try
        {
            var config = await _configService.ReadConfigAsync();

            var mode = config.TryGetValue("gpu_mode", out var mVal)
                ? mVal.GetInt32()
                : 0;

            var auto = config.TryGetValue("gpu_auto", out var aVal)
                && aVal.ValueKind == JsonValueKind.True;

            return Ok(new { mode, name = ModeNames.ElementAtOrDefault(mode) ?? "Unknown", auto });
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
            await _configService.WriteConfigAsync(new Dictionary<string, object>
            {
                ["gpu_mode"] = request.Mode,
                ["gpu_auto"] = request.Auto
            });

            await _processService.RestartGHelperAsync();

            return Ok(new
            {
                mode = request.Mode,
                name = ModeNames[request.Mode],
                auto = request.Auto,
                warning = request.Mode == 2
                    ? "Ultimate (MUX switch) mode requires a system reboot to take effect."
                    : (string?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GPU mode");
            return StatusCode(500, new { error = "Failed to set GPU mode" });
        }
    }
}
