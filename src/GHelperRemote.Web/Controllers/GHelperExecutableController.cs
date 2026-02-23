using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;

namespace GHelperRemote.Web.Controllers;

[ApiController]
[Route("api/ghelper/executable")]
public class GHelperExecutableController : ControllerBase
{
    private static readonly object AppSettingsWriteLock = new();

    private readonly GHelperProcessService _processService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<GHelperExecutableController> _logger;

    public GHelperExecutableController(
        GHelperProcessService processService,
        IWebHostEnvironment environment,
        ILogger<GHelperExecutableController> logger)
    {
        _processService = processService;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetExecutablePathStatus()
    {
        var configuredPath = _processService.GetConfiguredExecutablePath();
        var resolvedPath = _processService.GetResolvedExecutablePath();

        return Ok(new
        {
            configuredPath,
            resolvedPath,
            isResolved = !string.IsNullOrWhiteSpace(resolvedPath)
        });
    }

    [HttpPut]
    public IActionResult SetExecutablePath([FromBody] GHelperExecutablePathRequest request)
    {
        if (!_processService.SetConfiguredExecutablePath(request.Path, out var error))
            return BadRequest(new { error });

        var configuredPath = _processService.GetConfiguredExecutablePath();

        var persisted = false;
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            persisted = TryPersistExecutablePath(configuredPath, out var persistError);
            if (!persisted)
                warning = persistError;
        }

        return Ok(new
        {
            configuredPath,
            resolvedPath = configuredPath,
            isResolved = true,
            persisted,
            warning
        });
    }

    [HttpPost("auto-detect")]
    public IActionResult AutoDetectExecutablePath()
    {
        if (!_processService.TryAutoDetectExecutablePath(out var path) || string.IsNullOrWhiteSpace(path))
        {
            return NotFound(new
            {
                error = "Could not auto-detect GHelper.exe. Please set the full path manually."
            });
        }

        var persisted = TryPersistExecutablePath(path, out var warning);

        return Ok(new
        {
            configuredPath = path,
            resolvedPath = path,
            isResolved = true,
            persisted,
            warning
        });
    }

    private bool TryPersistExecutablePath(string path, out string? error)
    {
        try
        {
            var appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");
            if (!System.IO.File.Exists(appSettingsPath))
            {
                error = $"Could not persist path: appsettings file not found at {appSettingsPath}";
                return false;
            }

            lock (AppSettingsWriteLock)
            {
                var rawJson = System.IO.File.ReadAllText(appSettingsPath);
                var root = JsonNode.Parse(rawJson) as JsonObject;

                if (root is null)
                {
                    error = "Could not persist path: invalid appsettings.json format.";
                    return false;
                }

                var ghelperSection = root["GHelper"] as JsonObject;
                if (ghelperSection is null)
                {
                    ghelperSection = new JsonObject();
                    root["GHelper"] = ghelperSection;
                }

                ghelperSection["ExecutablePath"] = path;

                var updatedJson = root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                System.IO.File.WriteAllText(appSettingsPath, updatedJson);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist G-Helper executable path to appsettings.json");
            error = "Path applied for this session, but failed to persist to appsettings.json.";
            return false;
        }
    }
}
