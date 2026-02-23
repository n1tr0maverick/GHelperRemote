using GHelperRemote.Core.Acpi;
using GHelperRemote.Core.Models;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Reads hardware sensor data via the ASUS ATKACPI device driver.
/// Lazily creates and reuses a single AcpiDevice instance for the lifetime of the service.
/// </summary>
public sealed class AcpiSensorService : IDisposable
{
    private readonly ILogger<AcpiSensorService> _logger;
    private readonly object _deviceLock = new();
    private readonly object _sensorStateLock = new();
    private AcpiDevice? _device;
    private bool _disposed;
    private readonly Dictionary<uint, DateTime> _sensorNextRetryUtc = new();
    private readonly Dictionary<uint, DateTime> _sensorLastWarningUtc = new();

    private static readonly TimeSpan SensorRetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarningThrottleInterval = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<int, string> PerformanceModeNames = new()
    {
        { 0, "Balanced" },
        { 1, "Turbo" },
        { 2, "Silent" }
    };

    private static readonly Dictionary<int, string> GpuModeNames = new()
    {
        { 0, "Eco" },
        { 1, "Standard" },
        { 2, "Ultimate" }
    };

    public AcpiSensorService(ILogger<AcpiSensorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads all ACPI sensor values (CPU/GPU temperature, CPU/GPU fan RPM)
    /// and returns a populated <see cref="SystemStatus"/> model.
    /// Failed sensor reads return 0 and log a warning rather than throwing.
    /// </summary>
    public SystemStatus ReadSensors()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var device = GetOrCreateDevice();

        var status = new SystemStatus
        {
            CpuTemperature = ReadSensorSafe(device, AcpiConstants.CpuTemperature, "CPU temperature"),
            GpuTemperature = ReadSensorSafe(device, AcpiConstants.GpuTemperature, "GPU temperature"),
            CpuFanRpm = ReadSensorSafe(device, AcpiConstants.CpuFanSpeed, "CPU fan speed") * 100,
            GpuFanRpm = ReadSensorSafe(device, AcpiConstants.GpuFanSpeed, "GPU fan speed") * 100,
            Timestamp = DateTime.UtcNow
        };

        return status;
    }

    /// <summary>
    /// Maps a numeric performance mode value to its display name.
    /// Returns "Unknown" for unrecognized values.
    /// </summary>
    public static string GetPerformanceModeName(int mode)
    {
        return PerformanceModeNames.TryGetValue(mode, out var name) ? name : "Unknown";
    }

    /// <summary>
    /// Maps a numeric GPU mode value to its display name.
    /// Returns "Unknown" for unrecognized values.
    /// </summary>
    public static string GetGpuModeName(int mode)
    {
        return GpuModeNames.TryGetValue(mode, out var name) ? name : "Unknown";
    }

    private AcpiDevice GetOrCreateDevice()
    {
        if (_device is not null)
            return _device;

        lock (_deviceLock)
        {
            if (_device is not null)
                return _device;

            _logger.LogInformation("Opening ATKACPI device handle");
            _device = new AcpiDevice();
            return _device;
        }
    }

    private int ReadSensorSafe(AcpiDevice device, uint deviceId, string sensorName)
    {
        var now = DateTime.UtcNow;

        lock (_sensorStateLock)
        {
            if (_sensorNextRetryUtc.TryGetValue(deviceId, out var nextRetry) && now < nextRetry)
                return 0;
        }

        try
        {
            var value = device.QueryDsts(deviceId);

            lock (_sensorStateLock)
            {
                _sensorNextRetryUtc.Remove(deviceId);
            }

            return value;
        }
        catch (Exception ex)
        {
            var shouldLogWarning = false;

            lock (_sensorStateLock)
            {
                _sensorNextRetryUtc[deviceId] = now + SensorRetryBackoff;

                if (!_sensorLastWarningUtc.TryGetValue(deviceId, out var lastWarning) ||
                    now - lastWarning >= WarningThrottleInterval)
                {
                    _sensorLastWarningUtc[deviceId] = now;
                    shouldLogWarning = true;
                }
            }

            if (shouldLogWarning)
            {
                _logger.LogWarning(ex,
                    "Failed to read {SensorName} (device ID 0x{DeviceId:X8}). Retrying periodically.",
                    sensorName,
                    deviceId);
            }
            else
            {
                _logger.LogDebug(
                    "Skipping repeated {SensorName} read warning for device ID 0x{DeviceId:X8}",
                    sensorName,
                    deviceId);
            }

            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_deviceLock)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
