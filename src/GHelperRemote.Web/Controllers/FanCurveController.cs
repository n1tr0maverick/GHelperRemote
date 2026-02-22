using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/fans")]
public class FanCurveController : ControllerBase
{
    private readonly GHelperConfigService _configService;
    private readonly GHelperProcessService _processService;
    private readonly ILogger<FanCurveController> _logger;

    public FanCurveController(
        GHelperConfigService configService,
        GHelperProcessService processService,
        ILogger<FanCurveController> logger)
    {
        _configService = configService;
        _processService = processService;
        _logger = logger;
    }

    [HttpGet("{modeId:int}")]
    public async Task<IActionResult> GetFanCurve(int modeId)
    {
        try
        {
            var config = await _configService.ReadConfigAsync();

            FanCurveProfile? cpuProfile = null;
            FanCurveProfile? gpuProfile = null;

            if (config.TryGetValue($"fan_profile_cpu_{modeId}", out var cpuHex)
                && cpuHex.ValueKind == JsonValueKind.String)
            {
                cpuProfile = FanCurveProfile.FromHexString(cpuHex.GetString()!);
            }

            if (config.TryGetValue($"fan_profile_gpu_{modeId}", out var gpuHex)
                && gpuHex.ValueKind == JsonValueKind.String)
            {
                gpuProfile = FanCurveProfile.FromHexString(gpuHex.GetString()!);
            }

            return Ok(new { cpu = cpuProfile, gpu = gpuProfile });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read fan curve for mode {ModeId}", modeId);
            return StatusCode(500, new { error = $"Failed to read fan curve for mode {modeId}" });
        }
    }

    [HttpPut("{modeId:int}")]
    public async Task<IActionResult> SetFanCurve(int modeId, [FromBody] FanCurveRequest request)
    {
        if (request.Cpu == null || request.Gpu == null)
            return BadRequest(new { error = "Both cpu and gpu fan curve profiles are required" });

        var cpuValidation = ValidateFanCurve(request.Cpu, "CPU");
        if (cpuValidation != null)
            return BadRequest(new { error = cpuValidation });

        var gpuValidation = ValidateFanCurve(request.Gpu, "GPU");
        if (gpuValidation != null)
            return BadRequest(new { error = gpuValidation });

        try
        {
            await _configService.WriteConfigAsync(new Dictionary<string, object>
            {
                [$"fan_profile_cpu_{modeId}"] = request.Cpu.ToHexString(),
                [$"fan_profile_gpu_{modeId}"] = request.Gpu.ToHexString()
            });

            await _processService.RestartGHelperAsync();

            return Ok(new { cpu = request.Cpu, gpu = request.Gpu });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set fan curve for mode {ModeId}", modeId);
            return StatusCode(500, new { error = $"Failed to set fan curve for mode {modeId}" });
        }
    }

    private static string? ValidateFanCurve(FanCurveProfile profile, string label)
    {
        if (profile.Temperatures == null || profile.Speeds == null)
            return $"{label} fan curve must include temps and speeds arrays";

        if (profile.Temperatures.Length != 8 || profile.Speeds.Length != 8)
            return $"{label} fan curve must have exactly 8 temperature and 8 speed values";

        for (var i = 0; i < profile.Temperatures.Length; i++)
        {
            if (profile.Temperatures[i] < 20 || profile.Temperatures[i] > 110)
                return $"{label} temperature values must be between 20 and 110";

            if (profile.Speeds[i] > 100)
                return $"{label} speed values must be between 0 and 100";
        }

        for (var i = 1; i < profile.Temperatures.Length; i++)
        {
            if (profile.Temperatures[i] <= profile.Temperatures[i - 1])
                return $"{label} temperature values must be monotonically increasing";
        }

        for (var i = 1; i < profile.Speeds.Length; i++)
        {
            if (profile.Speeds[i] < profile.Speeds[i - 1])
                return $"{label} speed values must be monotonically non-decreasing";
        }

        return null;
    }
}

public class FanCurveRequest
{
    public FanCurveProfile? Cpu { get; set; }
    public FanCurveProfile? Gpu { get; set; }
}
