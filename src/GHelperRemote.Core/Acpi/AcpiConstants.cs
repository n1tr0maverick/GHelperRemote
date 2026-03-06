namespace GHelperRemote.Core.Acpi;

public static class AcpiConstants
{
    // ATKACPI device path
    public const string DevicePath = @"\\.\ATKACPI";

    // ACPI method identifiers (used inside the input buffer, NOT as IOCTL codes)
    public const uint IoctlDsts = 0x53545344; // "DSTS" - query device status (read)
    public const uint IoctlDevs = 0x53564544; // "DEVS" - set device value (write)

    // IOCTL control code for ATKACPI DeviceIoControl
    public const uint AtkmAcpiIoctl = 0x0022240C;

    // DSTS return value offset (driver adds 0x10000 as success flag)
    public const int DstsReturnOffset = 65536;

    // --- Sensor DSTS device IDs ---
    public const uint CpuTemperature = 0x00120094;
    public const uint GpuTemperature = 0x00120097;
    public const uint CpuFanSpeed = 0x00110013;
    public const uint GpuFanSpeed = 0x00110014;
    public const uint MidFanSpeed = 0x00110031;

    // --- Performance mode DEVS device IDs ---
    public const uint PerformanceMode = 0x00120075;     // ROG / TUF
    public const uint VivoBookMode = 0x00110019;         // Vivobook / Zenbook fallback

    // Performance mode values
    public const int ModeBalanced = 0;
    public const int ModeTurbo = 1;
    public const int ModeSilent = 2;

    // --- GPU control DEVS/DSTS device IDs ---
    public const uint GpuEcoRog = 0x00090020;            // Eco mode toggle (ROG)
    public const uint GpuEcoVivo = 0x00090120;           // Eco mode toggle (Vivobook)
    public const uint GpuMuxRog = 0x00090016;            // MUX switch (ROG)
    public const uint GpuMuxVivo = 0x00090026;           // MUX switch (Vivobook)

    // GPU mode values
    public const int GpuModeEco = 0;
    public const int GpuModeStandard = 1;
    public const int GpuModeUltimate = 2;

    // --- Battery ---
    public const uint BatteryChargeLimit = 0x00120057;

    // --- Fan curve DEVS device IDs ---
    public const uint DevsCpuFanCurve = 0x00110024;
    public const uint DevsGpuFanCurve = 0x00110025;
    public const uint DevsMidFanCurve = 0x00110032;

    // --- File access constants ---
    public const int FileShareRead = 0x00000001;
    public const int FileShareWrite = 0x00000002;
    public const int FileShareReadWrite = FileShareRead | FileShareWrite;
    public const int OpenExisting = 3;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileAttributeNormal = 0x00000080;
}
