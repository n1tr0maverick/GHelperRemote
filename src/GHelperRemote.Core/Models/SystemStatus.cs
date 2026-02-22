namespace GHelperRemote.Core.Models;

public class SystemStatus
{
    public int CpuTemperature { get; set; }
    public int GpuTemperature { get; set; }
    public int CpuFanRpm { get; set; }
    public int GpuFanRpm { get; set; }
    public int PerformanceMode { get; set; }
    public string PerformanceModeName { get; set; } = "";
    public int GpuMode { get; set; }
    public string GpuModeName { get; set; } = "";
    public BatteryStatus Battery { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
