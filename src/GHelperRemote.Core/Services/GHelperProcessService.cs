using System.Diagnostics;
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
    private string? _cachedExecutablePath;

    private const string ProcessName = "GHelper";
    private static readonly TimeSpan KillWaitDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartWaitDelay = TimeSpan.FromSeconds(2);

    public GHelperProcessService(ILogger<GHelperProcessService> logger)
    {
        _logger = logger;
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
            var executablePath = FindExecutablePath();
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
    private string? FindExecutablePath()
    {
        // Return cached path if still valid
        if (!string.IsNullOrEmpty(_cachedExecutablePath) && File.Exists(_cachedExecutablePath))
            return _cachedExecutablePath;

        // Try to get path from running process
        var path = GetPathFromRunningProcess();
        if (path is not null)
        {
            _cachedExecutablePath = path;
            return path;
        }

        // Fall back to common installation paths
        var commonPaths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GHelper",
                "GHelper.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GHelper",
                "GHelper.exe")
        };

        foreach (var candidate in commonPaths)
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("Found G-Helper at common path: {Path}", candidate);
                _cachedExecutablePath = candidate;
                return candidate;
            }
        }

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
