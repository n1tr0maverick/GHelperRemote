using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Reads and writes G-Helper's config.json with retry logic, in-memory caching,
/// atomic writes, and write serialization. Designed for concurrent access alongside
/// the G-Helper process which may also be reading/writing the same file.
/// </summary>
public sealed class GHelperConfigService : IDisposable
{
    private readonly ILogger<GHelperConfigService> _logger;
    private readonly string _configPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // In-memory cache
    private Dictionary<string, JsonElement>? _cache;
    private DateTime _cacheTimestamp;
    private readonly object _cacheLock = new();

    // Write cooldown: minimum 3 seconds between writes
    private DateTime _lastWriteTime = DateTime.MinValue;
    private static readonly TimeSpan WriteCooldown = TimeSpan.FromSeconds(3);

    // Retry configuration
    private const int MaxRetries = 3;
    private const int BaseRetryDelayMs = 200;

    /// <summary>
    /// Flag indicating a write was recently performed by this service.
    /// Used by <see cref="ConfigWatcherService"/> to debounce file-change notifications
    /// triggered by our own writes.
    /// </summary>
    public volatile bool RecentlyWritten;

    public GHelperConfigService(ILogger<GHelperConfigService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var configuredPath = configuration["GHelper:ConfigPath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            _configPath = Environment.ExpandEnvironmentVariables(configuredPath);
        }
        else
        {
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GHelper",
                "config.json");
        }

        _logger.LogInformation("G-Helper config path: {ConfigPath}", _configPath);
    }

    /// <summary>
    /// Reads the entire G-Helper configuration, using the in-memory cache when available.
    /// Falls back to disk with retry logic using FileShare.ReadWrite for concurrent access.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> ReadConfigAsync()
    {
        lock (_cacheLock)
        {
            if (_cache is not null)
                return new Dictionary<string, JsonElement>(_cache);
        }

        var config = await ReadConfigFromDiskAsync();

        lock (_cacheLock)
        {
            _cache = config;
            _cacheTimestamp = DateTime.UtcNow;
        }

        return new Dictionary<string, JsonElement>(config);
    }

    /// <summary>
    /// Reads a single typed value from the config by key.
    /// Returns default(T) if the key is not found.
    /// </summary>
    public async Task<T?> GetValueAsync<T>(string key)
    {
        var config = await ReadConfigAsync();

        if (!config.TryGetValue(key, out var element))
            return default;

        try
        {
            return element.Deserialize<T>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize config key '{Key}' as {Type}", key, typeof(T).Name);
            return default;
        }
    }

    /// <summary>
    /// Merges the provided updates into the existing config and writes atomically to disk.
    /// Uses write serialization via SemaphoreSlim and enforces a 3-second cooldown between writes.
    /// Writes to a .tmp file first, then atomically moves it into place.
    /// </summary>
    public async Task WriteConfigAsync(Dictionary<string, object> updates)
    {
        await _writeLock.WaitAsync();
        try
        {
            // Enforce write cooldown
            var timeSinceLastWrite = DateTime.UtcNow - _lastWriteTime;
            if (timeSinceLastWrite < WriteCooldown)
            {
                var delay = WriteCooldown - timeSinceLastWrite;
                _logger.LogDebug("Write cooldown active, waiting {Delay}ms", delay.TotalMilliseconds);
                await Task.Delay(delay);
            }

            // Read current config from disk (bypass cache to get freshest data for merge)
            var config = await ReadConfigFromDiskAsync();

            // Merge updates into existing config
            foreach (var (key, value) in updates)
            {
                var jsonElement = JsonSerializer.SerializeToElement(value);
                config[key] = jsonElement;
            }

            // Serialize the merged config
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, jsonOptions);

            // Atomic write: write to .tmp file, then move with overwrite
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);

            var tmpPath = _configPath + ".tmp";

            await File.WriteAllTextAsync(tmpPath, json);

            RecentlyWritten = true;
            File.Move(tmpPath, _configPath, overwrite: true);

            _lastWriteTime = DateTime.UtcNow;

            // Update cache with the new data
            lock (_cacheLock)
            {
                _cache = config;
                _cacheTimestamp = DateTime.UtcNow;
            }

            _logger.LogDebug("Config written successfully with {Count} updates", updates.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Invalidates the in-memory cache, forcing the next read to go to disk.
    /// Called by <see cref="ConfigWatcherService"/> when the config file changes externally.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache = null;
            _logger.LogDebug("Config cache invalidated");
        }
    }

    private async Task<Dictionary<string, JsonElement>> ReadConfigFromDiskAsync()
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Use FileShare.ReadWrite to allow concurrent access with G-Helper
                await using var stream = new FileStream(
                    _configPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                var document = await JsonDocument.ParseAsync(stream);
                var config = new Dictionary<string, JsonElement>();

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    // Clone each element so it survives after the JsonDocument is disposed
                    config[property.Name] = property.Value.Clone();
                }

                return config;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                var delay = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex,
                    "Failed to read config (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                    attempt, MaxRetries, delay);
                await Task.Delay(delay);
            }
        }

        // Final attempt - let exceptions propagate
        _logger.LogError("All retry attempts to read config have failed, performing final attempt");
        await using var finalStream = new FileStream(
            _configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        var finalDocument = await JsonDocument.ParseAsync(finalStream);
        var finalConfig = new Dictionary<string, JsonElement>();

        foreach (var property in finalDocument.RootElement.EnumerateObject())
        {
            finalConfig[property.Name] = property.Value.Clone();
        }

        return finalConfig;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }
}
