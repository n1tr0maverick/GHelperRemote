using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GHelperRemote.Core.Acpi;

/// <summary>
/// Managed wrapper around the ASUS ATKACPI device driver.
/// Matches GHelper's AsusACPI.cs CallMethod pattern for full compatibility.
/// Provides thread-safe access to DSTS (read) and DEVS (write) operations.
/// </summary>
public sealed class AcpiDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _ioLock = new();
    private bool _disposed;

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
    /// Core ACPI call method matching GHelper's AsusACPI.CallMethod.
    /// Input buffer: [MethodID(4)][ArgsLength(4)][Args(N)]
    /// Output buffer: 16 bytes, result in first 4 bytes.
    /// </summary>
    private int CallMethod(uint methodId, byte[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inBuffer = new byte[8 + args.Length];
        BitConverter.TryWriteBytes(inBuffer.AsSpan(0, 4), methodId);
        BitConverter.TryWriteBytes(inBuffer.AsSpan(4, 4), (uint)args.Length);
        args.CopyTo(inBuffer, 8);

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
                    $"DeviceIoControl failed for method 0x{methodId:X8}.");
            }
        }

        return BitConverter.ToInt32(outBuffer, 0);
    }

    /// <summary>
    /// Queries DSTS (device status) for the specified device ID.
    /// Subtracts the 0x10000 success flag from the result.
    /// Matches GHelper's DeviceGet().
    /// </summary>
    public int QueryDsts(uint deviceId)
    {
        var args = new byte[8];
        BitConverter.TryWriteBytes(args.AsSpan(0, 4), deviceId);
        // Bytes 4-7 remain 0 (padding)
        return CallMethod(AcpiConstants.IoctlDsts, args) - AcpiConstants.DstsReturnOffset;
    }

    /// <summary>
    /// Sends a DEVS (set device value) command with a single integer value.
    /// Args: [DeviceID(4)][Value(4)] = 8 bytes.
    /// Matches GHelper's DeviceSet(uint, int).
    /// </summary>
    public int CallDevs(uint deviceId, int value)
    {
        var args = new byte[8];
        BitConverter.TryWriteBytes(args.AsSpan(0, 4), deviceId);
        BitConverter.TryWriteBytes(args.AsSpan(4, 4), value);
        return CallMethod(AcpiConstants.IoctlDevs, args);
    }

    /// <summary>
    /// Sends a DEVS command with arbitrary data (e.g. 16-byte fan curves).
    /// Args: [DeviceID(4)][Data(N)] = 4+N bytes.
    /// Matches GHelper's DeviceSet(uint, byte[]).
    /// </summary>
    public int CallDevsWithData(uint deviceId, byte[] data)
    {
        var args = new byte[4 + data.Length];
        BitConverter.TryWriteBytes(args.AsSpan(0, 4), deviceId);
        data.CopyTo(args, 4);
        return CallMethod(AcpiConstants.IoctlDevs, args);
    }

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
