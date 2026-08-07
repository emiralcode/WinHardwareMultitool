namespace WinHardwareMultitool.Models;

public sealed class SafetyThresholds
{
    public double WarningTempC { get; set; } = 80.0;
    public double CriticalTempC { get; set; } = 90.0;
}
