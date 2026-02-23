using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Manages the G-Helper process lifecycle, providing methods to check its status,
/// find its executable path, and restart it when needed.
/// </summary>
public sealed class GHelperProcessService
{
    private readonly ILogger<GHelperProcessService> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private readonly object _pathLock = new();
    private string? _cachedExecutablePath;
    private string? _configuredExecutablePath;

    private const string ProcessName = "GHelper";
    private static readonly TimeSpan KillWaitDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartWaitDelay = TimeSpan.FromSeconds(2);

    public GHelperProcessService(ILogger<GHelperProcessService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var configuredPath = configuration["GHelper:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            _configuredExecutablePath = Environment.ExpandEnvironmentVariables(configuredPath);
            _logger.LogInformation("Configured G-Helper executable path: {Path}", _configuredExecutablePath);
        }
    }

    public string? GetConfiguredExecutablePath()
    {
        lock (_pathLock)
        {
            return _configuredExecutablePath;
        }
    }

    public bool SetConfiguredExecutablePath(string path, out string? error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
        var fullPath = Path.GetFullPath(expandedPath);

        if (!File.Exists(fullPath))
        {
            error = "G-Helper executable path does not exist.";
            return false;
        }

        if (!string.Equals(Path.GetFileName(fullPath), "GHelper.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Path must point to GHelper.exe.";
            return false;
        }

        lock (_pathLock)
        {
            _configuredExecutablePath = fullPath;
            _cachedExecutablePath = fullPath;
        }

        _logger.LogInformation("Updated G-Helper executable path to: {Path}", fullPath);
        error = null;
        return true;
    }

    public string? GetResolvedExecutablePath()
    {
        return FindExecutablePath(logWarnings: false, updateConfiguredPath: false);
    }

    public bool TryAutoDetectExecutablePath(out string? path)
    {
        path = FindExecutablePath(logWarnings: false, updateConfiguredPath: true);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        lock (_pathLock)
        {
            _configuredExecutablePath = path;
            _cachedExecutablePath = path;
        }

        _logger.LogInformation("Auto-detected G-Helper executable path: {Path}", path);
        return true;
    }

    /// <summary>
    /// Checks whether any G-Helper process is currently running.
    /// </summary>
    public bool IsGHelperRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(ProcessName);
            var running = processes.Length > 0;

            foreach (var process in processes)
                process.Dispose();

            return running;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if G-Helper is running");
            return false;
        }
    }

    /// <summary>
    /// Kills all running G-Helper processes, waits briefly, then restarts the application.
    /// Uses a SemaphoreSlim to prevent concurrent restart attempts.
    /// </summary>
    public async Task RestartGHelperAsync()
    {
        if (!await _restartLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogWarning("Restart already in progress, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("Restarting G-Helper");

            // Find the executable path before killing the process
            var executablePath = FindExecutablePath(logWarnings: true, updateConfiguredPath: true);
            if (string.IsNullOrEmpty(executablePath))
            {
                _logger.LogError("Cannot restart G-Helper: executable path not found");
                throw new FileNotFoundException(
                    "G-Helper executable not found. Ensure G-Helper is installed and has been run at least once.");
            }

            // Kill all running G-Helper processes
            var processes = Process.GetProcessesByName(ProcessName);
            foreach (var process in processes)
            {
                try
                {
                    _logger.LogDebug("Killing G-Helper process (PID: {Pid})", process.Id);
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill G-Helper process (PID: {Pid})", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Wait for processes to fully terminate
            await Task.Delay(KillWaitDelay);

            // Start G-Helper
            _logger.LogInformation("Starting G-Helper from: {Path}", executablePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ""
            };

            Process.Start(startInfo);

            // Wait for G-Helper to initialize
            await Task.Delay(StartWaitDelay);

            _logger.LogInformation("G-Helper restart completed");
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// Attempts to find the G-Helper executable path.
    /// First checks running processes, then falls back to common installation paths.
    /// Caches the result once found.
    /// </summary>
    private string? FindExecutablePath(bool logWarnings, bool updateConfiguredPath)
    {
        string? configuredPath;
        lock (_pathLock)
        {
            configuredPath = _configuredExecutablePath;
        }

        // Prefer explicit configuration
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return configuredPath;

            if (logWarnings)
            {
                _logger.LogWarning(
                    "Configured G-Helper executable path does not exist: {Path}",
                    configuredPath);
            }
        }

        // Return cached path if still valid
        if (!string.IsNullOrEmpty(_cachedExecutablePath) && File.Exists(_cachedExecutablePath))
            return _cachedExecutablePath;

        // Try to get path from running process
        var path = GetPathFromRunningProcess();
        if (path is not null)
        {
            _cachedExecutablePath = path;
            if (updateConfiguredPath)
            {
                lock (_pathLock)
                {
                    _configuredExecutablePath = path;
                }
            }

            return path;
        }

        // Fall back to common installation paths
        var commonPaths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "GHelper",
                "GHelper.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GHelper",
                "GHelper.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GHelper",
                "GHelper.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "GHelper",
                "GHelper.exe")
        };

        foreach (var candidate in commonPaths)
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("Found G-Helper at common path: {Path}", candidate);
                _cachedExecutablePath = candidate;

                if (updateConfiguredPath)
                {
                    lock (_pathLock)
                    {
                        _configuredExecutablePath = candidate;
                    }
                }

                return candidate;
            }
        }

        if (logWarnings)
            _logger.LogWarning("G-Helper executable not found in running processes or common paths");

        return null;
    }

    private string? GetPathFromRunningProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName(ProcessName);
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        _logger.LogDebug("Found G-Helper path from running process: {Path}", path);
                        return path;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not get module info from G-Helper process (PID: {Pid})", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate G-Helper processes");
        }

        return null;
    }
}
