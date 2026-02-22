namespace GHelperRemote.Core.Acpi;

public static class AcpiConstants
{
    // ATKACPI device path
    public const string DevicePath = @"\\.\ATKACPI";

    // IOCTL codes
    public const uint IoctlDsts = 0x53545344; // DSTS - query device status (read-only)
    public const uint IoctlDevs = 0x53564544; // DEVS - set device value (write)

    // IOCTL control code for ATKACPI
    public const uint AtkmAcpiIoctl = 0x0022240C;

    // DSTS return value offset (driver adds 0x10000 as success flag)
    public const int DstsReturnOffset = 65536;

    // Sensor device IDs for DSTS queries
    public const uint CpuTemperature = 0x00120094;
    public const uint GpuTemperature = 0x00120097;
    public const uint CpuFanSpeed = 0x00110013;
    public const uint GpuFanSpeed = 0x00110014;

    // DEVS device IDs for fan curve writes
    public const uint DevsCpuFanCurve = 0x00110024;
    public const uint DevsGpuFanCurve = 0x00110025;

    // Performance mode values
    public const uint PerformanceBalanced = 0;
    public const uint PerformanceTurbo = 1;
    public const uint PerformanceSilent = 2;

    // GPU mode values
    public const int GpuModeEco = 0;
    public const int GpuModeStandard = 1;
    public const int GpuModeUltimate = 2;

    // File sharing for concurrent access with G-Helper
    public const int FileShareRead = 0x00000001;
    public const int FileShareWrite = 0x00000002;
    public const int FileShareReadWrite = FileShareRead | FileShareWrite;
    public const int OpenExisting = 3;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileAttributeNormal = 0x00000080;
}
