using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Acpi;
using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/fans")]
public class FanCurveController : ControllerBase
{
    private readonly AcpiSensorService _acpiService;
    private readonly GHelperConfigService _configService;
    private readonly ILogger<FanCurveController> _logger;

    public FanCurveController(
        AcpiSensorService acpiService,
        GHelperConfigService configService,
        ILogger<FanCurveController> logger)
    {
        _acpiService = acpiService;
        _configService = configService;
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
            // Build 16-byte curve data: 8 temps + 8 speeds
            var cpuCurveData = BuildCurveData(request.Cpu);
            var gpuCurveData = BuildCurveData(request.Gpu);

            // Apply directly to hardware via ACPI
            var cpuOk = _acpiService.ApplyFanCurve(AcpiConstants.DevsCpuFanCurve, cpuCurveData);
            var gpuOk = _acpiService.ApplyFanCurve(AcpiConstants.DevsGpuFanCurve, gpuCurveData);

            if (!cpuOk || !gpuOk)
            {
                var failed = (!cpuOk && !gpuOk) ? "CPU and GPU" : (!cpuOk ? "CPU" : "GPU");
                _logger.LogWarning("Fan curve ACPI apply failed for: {Failed}", failed);
            }

            // Persist to config
            string? warning = null;
            try
            {
                await _configService.WriteConfigAsync(new Dictionary<string, object>
                {
                    [$"fan_profile_cpu_{modeId}"] = request.Cpu.ToHexString(),
                    [$"fan_profile_gpu_{modeId}"] = request.Gpu.ToHexString()
                });
            }
            catch (Exception configEx)
            {
                _logger.LogWarning(configEx, "Fan curves applied to hardware but config write failed");
                warning = "Fan curves applied to hardware but failed to persist to config.";
            }

            if (!cpuOk || !gpuOk)
            {
                warning = "Fan curves saved to config but ACPI apply failed for some curves. " +
                          "The curves may take effect after the next mode change.";
            }

            return Ok(new { cpu = request.Cpu, gpu = request.Gpu, warning });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set fan curve for mode {ModeId}", modeId);
            return StatusCode(500, new { error = $"Failed to set fan curve for mode {modeId}" });
        }
    }

    private static byte[] BuildCurveData(FanCurveProfile profile)
    {
        var data = new byte[16];
        Array.Copy(profile.Temperatures!, 0, data, 0, 8);
        Array.Copy(profile.Speeds!, 0, data, 8, 8);
        return data;
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
