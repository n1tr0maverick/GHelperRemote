using GHelperRemote.Core.Acpi;
using GHelperRemote.Core.Models;
using Microsoft.Extensions.Logging;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Provides read AND write access to ASUS ATKACPI hardware.
/// Reads: sensor data (temperatures, fan speeds).
/// Writes: performance mode, GPU mode, battery charge limit, fan curves.
/// All operations go through the single ATKACPI device driver.
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

    // ========================================================================
    // SENSOR READS (DSTS queries)
    // ========================================================================

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
    /// Raw DSTS query. Returns the value or null if the query fails.
    /// </summary>
    public int? QueryDevice(uint deviceId)
    {
        try
        {
            var device = GetOrCreateDevice();
            return device.QueryDsts(deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ACPI DSTS query failed for device 0x{DeviceId:X8}", deviceId);
            return null;
        }
    }

    // ========================================================================
    // HARDWARE CONTROL (DEVS calls) - matches GHelper's AsusACPI behavior
    // ========================================================================

    /// <summary>
    /// Sets performance mode via ACPI DEVS call.
    /// Tries ROG endpoint (0x00120075) first, falls back to Vivobook (0x00110019).
    /// This is exactly what GHelper does in ModeControl.SetPerformanceMode().
    /// </summary>
    public bool SetPerformanceMode(int mode)
    {
        try
        {
            var device = GetOrCreateDevice();
            device.CallDevs(AcpiConstants.PerformanceMode, mode);
            _logger.LogInformation("Performance mode set to {Mode} ({Name}) via ACPI [0x00120075]",
                mode, GetPerformanceModeName(mode));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ROG performance mode endpoint failed, trying Vivobook endpoint");
            try
            {
                var device = GetOrCreateDevice();
                device.CallDevs(AcpiConstants.VivoBookMode, mode);
                _logger.LogInformation("Performance mode set to {Mode} ({Name}) via Vivobook ACPI [0x00110019]",
                    mode, GetPerformanceModeName(mode));
                return true;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to set performance mode via any ACPI endpoint");
                return false;
            }
        }
    }

    /// <summary>
    /// Reads current GPU mode from ACPI.
    /// Logic matches GHelper's InitGPUMode():
    ///   MUX == 0 → Ultimate; GPUEco == 1 → Eco; else → Standard
    /// </summary>
    public int GetGpuMode()
    {
        // Check MUX first (try ROG then Vivobook)
        var mux = QueryDevice(AcpiConstants.GpuMuxRog) ?? QueryDevice(AcpiConstants.GpuMuxVivo);
        if (mux is 0)
        {
            _logger.LogDebug("GPU MUX = 0 → Ultimate mode");
            return AcpiConstants.GpuModeUltimate;
        }

        // Check Eco (try ROG then Vivobook)
        var eco = QueryDevice(AcpiConstants.GpuEcoRog) ?? QueryDevice(AcpiConstants.GpuEcoVivo);
        if (eco is 1)
        {
            _logger.LogDebug("GPU Eco = 1 → Eco mode");
            return AcpiConstants.GpuModeEco;
        }

        _logger.LogDebug("GPU → Standard mode (MUX={Mux}, Eco={Eco})", mux, eco);
        return AcpiConstants.GpuModeStandard;
    }

    /// <summary>
    /// Sets GPU Eco mode on/off. Tries ROG endpoint first, then Vivobook.
    /// eco=1 enables Eco (dGPU off), eco=0 disables Eco (dGPU on → Standard mode).
    /// </summary>
    public bool SetGpuEco(int eco)
    {
        try
        {
            var device = GetOrCreateDevice();
            device.CallDevs(AcpiConstants.GpuEcoRog, eco);
            _logger.LogInformation("GPU Eco set to {Eco} via ROG endpoint", eco);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ROG GPU Eco endpoint failed, trying Vivobook");
            try
            {
                var device = GetOrCreateDevice();
                device.CallDevs(AcpiConstants.GpuEcoVivo, eco);
                _logger.LogInformation("GPU Eco set to {Eco} via Vivobook endpoint", eco);
                return true;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to set GPU Eco mode via any endpoint");
                return false;
            }
        }
    }

    /// <summary>
    /// Sets GPU MUX switch (for Ultimate mode). Requires reboot.
    /// value=0 → Ultimate (dGPU direct), value=1 → Hybrid (normal).
    /// </summary>
    public bool SetGpuMux(int value)
    {
        try
        {
            var device = GetOrCreateDevice();
            device.CallDevs(AcpiConstants.GpuMuxRog, value);
            _logger.LogInformation("GPU MUX set to {Value} via ROG endpoint", value);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ROG GPU MUX endpoint failed, trying Vivobook");
            try
            {
                var device = GetOrCreateDevice();
                device.CallDevs(AcpiConstants.GpuMuxVivo, value);
                _logger.LogInformation("GPU MUX set to {Value} via Vivobook endpoint", value);
                return true;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to set GPU MUX via any endpoint");
                return false;
            }
        }
    }

    /// <summary>
    /// Sets battery charge limit via ACPI DEVS call (device 0x00120057).
    /// Matches GHelper's battery limit handling.
    /// </summary>
    public bool SetBatteryChargeLimit(int limit)
    {
        try
        {
            var device = GetOrCreateDevice();
            device.CallDevs(AcpiConstants.BatteryChargeLimit, limit);
            _logger.LogInformation("Battery charge limit set to {Limit}% via ACPI", limit);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set battery charge limit via ACPI");
            return false;
        }
    }

    /// <summary>
    /// Applies a fan curve via ACPI DEVS call.
    /// curveData is 16 bytes: 8 temperature points + 8 speed percentages.
    /// Matches GHelper's fan curve application.
    /// </summary>
    public bool ApplyFanCurve(uint fanDeviceId, byte[] curveData)
    {
        if (curveData.Length != 16)
        {
            _logger.LogError("Fan curve data must be 16 bytes, got {Length}", curveData.Length);
            return false;
        }

        try
        {
            var device = GetOrCreateDevice();
            device.CallDevsWithData(fanDeviceId, curveData);
            _logger.LogInformation("Fan curve applied to device 0x{DeviceId:X8}", fanDeviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply fan curve to device 0x{DeviceId:X8}", fanDeviceId);
            return false;
        }
    }

    // ========================================================================
    // HELPERS
    // ========================================================================

    public static string GetPerformanceModeName(int mode)
    {
        return PerformanceModeNames.TryGetValue(mode, out var name) ? name : "Unknown";
    }

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
