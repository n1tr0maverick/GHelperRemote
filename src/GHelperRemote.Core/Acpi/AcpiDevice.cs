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
            AcpiConstants.FileAttributeNormal,
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
    /// G-Helper's actual buffer format is 16 bytes:
    ///   [0..3]  Method ID (DSTS = 0x53545344)
    ///   [4..7]  Args length = 8
    ///   [8..11] Device ID
    ///   [12..15] Padding = 0
    /// The return value has 65536 (0x10000) added as a success flag by the driver.
    /// </summary>
    public int QueryDsts(uint deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Build the input buffer: 16 bytes (matches G-Helper's AsusACPI.cs format)
        var inBuffer = new byte[16];
        BitConverter.TryWriteBytes(inBuffer.AsSpan(0, 4), AcpiConstants.IoctlDsts);  // Method ID
        BitConverter.TryWriteBytes(inBuffer.AsSpan(4, 4), (uint)8);                   // Args length
        BitConverter.TryWriteBytes(inBuffer.AsSpan(8, 4), deviceId);                   // Device ID
        // Bytes 12-15 remain 0 (padding)

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

        // Subtract 65536 (0x10000) - the driver adds this as a success flag
        return BitConverter.ToInt32(outBuffer, 0) - AcpiConstants.DstsReturnOffset;
    }

    /// <summary>
    /// Sends a DEVS (set device value) command to the ATKACPI driver.
    /// Used for writing hardware settings like fan curves.
    /// </summary>
    public void CallDevs(uint deviceId, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inBuffer = new byte[16];
        BitConverter.TryWriteBytes(inBuffer.AsSpan(0, 4), AcpiConstants.IoctlDevs);  // Method ID
        BitConverter.TryWriteBytes(inBuffer.AsSpan(4, 4), (uint)8);                   // Args length
        BitConverter.TryWriteBytes(inBuffer.AsSpan(8, 4), deviceId);                   // Device ID
        BitConverter.TryWriteBytes(inBuffer.AsSpan(12, 4), value);                     // Value to set

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
                    $"DeviceIoControl failed for DEVS call with device ID 0x{deviceId:X8}, value 0x{value:X8}.");
            }
        }
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
