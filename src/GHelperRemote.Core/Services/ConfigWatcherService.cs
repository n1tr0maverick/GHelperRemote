using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Background service that monitors G-Helper's config.json for external changes
/// using <see cref="FileSystemWatcher"/>. When a change is detected (and it was not
/// caused by our own writes), the in-memory cache in <see cref="GHelperConfigService"/>
/// is invalidated so the next read fetches fresh data from disk.
/// </summary>
public sealed class ConfigWatcherService : BackgroundService, IDisposable
{
    private readonly GHelperConfigService _configService;
    private readonly ILogger<ConfigWatcherService> _logger;
    private readonly string _configDirectory;
    private readonly string _configFileName;

    private FileSystemWatcher? _watcher;
    private DateTime _lastNotification = DateTime.MinValue;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    public ConfigWatcherService(
        GHelperConfigService configService,
        ILogger<ConfigWatcherService> logger,
        IConfiguration configuration)
    {
        _configService = configService;
        _logger = logger;

        var configuredPath = configuration["GHelper:ConfigPath"];
        string fullPath;
        if (!string.IsNullOrEmpty(configuredPath))
        {
            fullPath = Environment.ExpandEnvironmentVariables(configuredPath);
        }
        else
        {
            fullPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GHelper",
                "config.json");
        }

        _configDirectory = Path.GetDirectoryName(fullPath)!;
        _configFileName = Path.GetFileName(fullPath);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_configDirectory))
        {
            _logger.LogWarning(
                "Config directory does not exist, creating it: {Directory}", _configDirectory);
            Directory.CreateDirectory(_configDirectory);
        }

        _watcher = new FileSystemWatcher(_configDirectory)
        {
            Filter = _configFileName,
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Error += OnWatcherError;

        _logger.LogInformation(
            "Config watcher started, monitoring: {Directory}\\{FileName}",
            _configDirectory, _configFileName);

        // Register cancellation to clean up the watcher
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Config watcher stopping");
            DisposeWatcher();
        });

        // FileSystemWatcher runs on its own thread, so we complete immediately
        return Task.CompletedTask;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: ignore changes caused by our own writes
        if (_configService.RecentlyWritten)
        {
            _configService.RecentlyWritten = false;
            _logger.LogDebug("Ignoring config change triggered by our own write");
            return;
        }

        // Debounce: ignore rapid duplicate notifications
        var now = DateTime.UtcNow;
        if (now - _lastNotification < DebounceInterval)
        {
            _logger.LogDebug("Ignoring config change within debounce interval");
            return;
        }

        _lastNotification = now;

        _logger.LogInformation("External config change detected, invalidating cache");
        _configService.InvalidateCache();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogError(ex, "FileSystemWatcher error occurred");

        // Attempt to restart the watcher
        try
        {
            DisposeWatcher();

            _watcher = new FileSystemWatcher(_configDirectory)
            {
                Filter = _configFileName,
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnConfigFileChanged;
            _watcher.Error += OnWatcherError;

            _logger.LogInformation("FileSystemWatcher restarted after error");
        }
        catch (Exception restartEx)
        {
            _logger.LogError(restartEx, "Failed to restart FileSystemWatcher");
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public override void Dispose()
    {
        DisposeWatcher();
        base.Dispose();
    }
}
