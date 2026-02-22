namespace GHelperRemote.Core.Models;

public class BatteryStatus
{
    public int ChargePercent { get; set; }
    public bool IsCharging { get; set; }
    public int ChargeLimit { get; set; }
    public int DesignCapacity { get; set; }
    public int FullChargeCapacity { get; set; }
    public int HealthPercent { get; set; }
    public int ChargeRate { get; set; }
    public int DischargeRate { get; set; }
}
