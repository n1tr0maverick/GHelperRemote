using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private static readonly TimeSpan KillWaitDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StartWaitDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

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
    /// Kills all running G-Helper processes, waits for them to fully exit, then restarts
    /// the application. Uses a SemaphoreSlim to prevent concurrent restart attempts.
    /// Verifies the new process actually started successfully.
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
                    "G-Helper executable not found. Ensure G-Helper is installed and has been run at least once, " +
                    "or set the executable path manually.");
            }

            // Kill all running G-Helper processes and wait for each to fully exit
            await KillAllGHelperProcessesAsync();

            // Additional delay after all processes confirmed dead
            await Task.Delay(KillWaitDelay);

            // Verify no GHelper processes remain
            var remaining = Process.GetProcessesByName(ProcessName);
            if (remaining.Length > 0)
            {
                _logger.LogWarning("G-Helper processes still running after kill, force-killing stragglers");
                foreach (var p in remaining)
                {
                    try { p.Kill(entireProcessTree: true); }
                    catch { /* best effort */ }
                    finally { p.Dispose(); }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            // Start G-Helper
            _logger.LogInformation("Starting G-Helper from: {Path}", executablePath);
            var process = StartGHelperProcess(executablePath);

            if (process is null)
            {
                _logger.LogError("Process.Start returned null for G-Helper");
                throw new InvalidOperationException(
                    "Failed to start G-Helper. The process could not be created.");
            }

            // Wait for G-Helper to initialize
            await Task.Delay(StartWaitDelay);

            // Verify it's actually running (it may have exited immediately)
            if (process.HasExited)
            {
                _logger.LogWarning(
                    "G-Helper process exited immediately with code {ExitCode}, attempting alternative launch",
                    process.ExitCode);
                process.Dispose();

                // Try alternative launch method
                process = StartGHelperProcessAlternative(executablePath);
                if (process is not null)
                {
                    await Task.Delay(StartWaitDelay);
                    if (process.HasExited)
                    {
                        _logger.LogError(
                            "G-Helper exited again with code {ExitCode}",
                            process.ExitCode);
                        process.Dispose();
                    }
                }
            }
            else
            {
                process.Dispose();
            }

            // Final verification: is any GHelper process running?
            if (!IsGHelperRunning())
            {
                _logger.LogWarning("G-Helper does not appear to be running after restart attempt");
            }
            else
            {
                _logger.LogInformation("G-Helper restart completed successfully");
            }
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task KillAllGHelperProcessesAsync()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0)
        {
            _logger.LogDebug("No G-Helper processes to kill");
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                _logger.LogDebug("Killing G-Helper process (PID: {Pid})", process.Id);
                process.Kill(entireProcessTree: true);

                // Wait for this specific process to exit with a timeout
                var exited = await WaitForExitAsync(process, ProcessExitTimeout);
                if (!exited)
                {
                    _logger.LogWarning(
                        "G-Helper process (PID: {Pid}) did not exit within timeout", process.Id);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited
                _logger.LogDebug("G-Helper process (PID: {Pid}) already exited", process.Id);
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
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process already exited
            return true;
        }
    }

    /// <summary>
    /// Primary launch method: UseShellExecute = false for better control and compatibility
    /// when running as a Windows Service (no interactive desktop required).
    /// </summary>
    private Process? StartGHelperProcess(string executablePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false,
            };

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary launch method failed for G-Helper");
            return null;
        }
    }

    /// <summary>
    /// Alternative launch method: UseShellExecute = true as a fallback if the primary
    /// method fails (e.g., when the app requires shell execution for elevation or COM).
    /// </summary>
    private Process? StartGHelperProcessAlternative(string executablePath)
    {
        try
        {
            _logger.LogInformation("Trying alternative launch for G-Helper: {Path}", executablePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
            };

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alternative launch method also failed for G-Helper");
            return null;
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

        // Fall back to common installation paths.
        // When running as a Windows Service (LocalSystem), SpecialFolder paths resolve to
        // the system profile, not the logged-in user. We also probe common user profile paths
        // by reading the USERPROFILE environment variable and enumerating C:\Users\*.
        var commonPaths = BuildCandidatePaths();

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

    /// <summary>
    /// Builds a comprehensive list of candidate paths for GHelper.exe.
    /// Covers standard SpecialFolder locations, plus probes all user profiles
    /// under C:\Users (needed when running as LocalSystem service).
    /// </summary>
    private List<string> BuildCandidatePaths()
    {
        var candidates = new List<string>();

        // Standard .NET SpecialFolder paths (work when running as current user)
        var baseDirs = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GHelper"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GHelper", ""),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GHelper", ""),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GHelper", ""),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GHelper"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper", ""),
        };

        foreach (var (root, sub1, sub2) in baseDirs)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = string.IsNullOrEmpty(sub2) ? Path.Combine(root, sub1) : Path.Combine(root, sub1, sub2);
            candidates.Add(Path.Combine(dir, "GHelper.exe"));
        }

        // When running as LocalSystem, enumerate actual user profiles
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var usersDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3),
                    "Users");

                if (Directory.Exists(usersDir))
                {
                    foreach (var userDir in Directory.EnumerateDirectories(usersDir))
                    {
                        var userName = Path.GetFileName(userDir);
                        // Skip system/default profiles
                        if (string.Equals(userName, "Public", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(userName, "Default", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(userName, "Default User", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(userName, "All Users", StringComparison.OrdinalIgnoreCase))
                            continue;

                        candidates.Add(Path.Combine(userDir, "Downloads", "GHelper", "GHelper.exe"));
                        candidates.Add(Path.Combine(userDir, "AppData", "Local", "GHelper", "GHelper.exe"));
                        candidates.Add(Path.Combine(userDir, "AppData", "Local", "Programs", "GHelper", "GHelper.exe"));
                        candidates.Add(Path.Combine(userDir, "AppData", "Roaming", "GHelper", "GHelper.exe"));
                        candidates.Add(Path.Combine(userDir, "Desktop", "GHelper", "GHelper.exe"));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not enumerate user profile directories");
            }
        }

        // Deduplicate while preserving order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in candidates)
        {
            if (seen.Add(path))
                result.Add(path);
        }

        return result;
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
