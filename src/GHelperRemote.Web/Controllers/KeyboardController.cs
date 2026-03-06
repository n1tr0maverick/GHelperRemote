using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/keyboard")]
public class KeyboardController : ControllerBase
{
    private readonly GHelperConfigService _configService;
    private readonly ILogger<KeyboardController> _logger;

    public KeyboardController(
        GHelperConfigService configService,
        ILogger<KeyboardController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetKeyboardSettings()
    {
        try
        {
            var config = await _configService.ReadConfigAsync();

            return Ok(new
            {
                brightness = config.TryGetValue("kbd_brightness", out var bVal) ? bVal.GetInt32() : 0,
                mode = config.TryGetValue("kbd_mode", out var mVal) ? mVal.GetInt32() : 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read keyboard settings");
            return StatusCode(500, new { error = "Failed to read keyboard settings" });
        }
    }

    [HttpPut]
    public async Task<IActionResult> SetKeyboardSettings([FromBody] KeyboardSettings settings)
    {
        if (settings.Brightness < 0 || settings.Brightness > 3)
            return BadRequest(new { error = "Keyboard brightness must be between 0 and 3" });

        try
        {
            await _configService.WriteConfigAsync(new Dictionary<string, object>
            {
                ["kbd_brightness"] = settings.Brightness,
                ["kbd_mode"] = settings.Mode
            });

            return Ok(new { brightness = settings.Brightness, mode = settings.Mode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set keyboard settings");
            return StatusCode(500, new { error = "Failed to set keyboard settings" });
        }
    }
}
