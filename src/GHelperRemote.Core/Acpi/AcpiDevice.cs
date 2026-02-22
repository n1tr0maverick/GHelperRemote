using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GHelperRemote.Core.Acpi;

/// <summary>
/// Managed wrapper around the ASUS ATKACPI device driver.
/// Provides thread-safe access to DSTS (device status) queries for reading
/// sensor data such as CPU/GPU temperatures and fan speeds.
/// </summary>
public sealed class AcpiDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _ioLock = new();
    private bool _disposed;

    /// <summary>
    /// Opens a handle to the ATKACPI device driver.
    /// Uses READ|WRITE share mode to allow concurrent access with G-Helper.
    /// </summary>
    /// <exception cref="Win32Exception">Thrown when the device handle cannot be opened.</exception>
    public AcpiDevice()
    {
        _handle = AcpiNativeMethods.CreateFileW(
            AcpiConstants.DevicePath,
            AcpiConstants.GenericRead | AcpiConstants.GenericWrite,
            AcpiConstants.FileShareReadWrite,
            IntPtr.Zero,
            AcpiConstants.OpenExisting,
            0,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Failed to open ATKACPI device at {AcpiConstants.DevicePath}. " +
                "Ensure ASUS system drivers are installed and the application is running with administrator privileges.");
        }
    }

    /// <summary>
    /// Queries the ATKACPI device status (DSTS) for the specified sensor device ID.
    /// </summary>
    /// <param name="deviceId">
    /// The sensor device ID to query (e.g., <see cref="AcpiConstants.CpuTemperature"/>).
    /// </param>
    /// <returns>The raw uint result from the DSTS query.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the device has been disposed.</exception>
    /// <exception cref="Win32Exception">Thrown if the DeviceIoControl call fails.</exception>
    public uint QueryDsts(uint deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Build the input buffer: 8 bytes
        // Bytes 0-3: DSTS IOCTL code (little-endian)
        // Bytes 4-7: Device ID (little-endian)
        var inBuffer = new byte[8];
        BitConverter.TryWriteBytes(inBuffer.AsSpan(0, 4), AcpiConstants.IoctlDsts);
        BitConverter.TryWriteBytes(inBuffer.AsSpan(4, 4), deviceId);

        var outBuffer = new byte[16];

        lock (_ioLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            bool success = AcpiNativeMethods.DeviceIoControl(
                _handle.DangerousGetHandle(),
                AcpiConstants.AtkmAcpiIoctl,
                inBuffer,
                (uint)inBuffer.Length,
                outBuffer,
                (uint)outBuffer.Length,
                out _,
                IntPtr.Zero);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"DeviceIoControl failed for DSTS query with device ID 0x{deviceId:X8}.");
            }
        }

        return BitConverter.ToUInt32(outBuffer, 0);
    }

    /// <summary>
    /// Releases the ATKACPI device handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_handle.IsInvalid && !_handle.IsClosed)
        {
            _handle.Close();
        }
    }
}
