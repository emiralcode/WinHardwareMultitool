namespace WinHardwareMultitool.Models;

/// <summary>Persisted between runs in %AppData%\WinHardwareMultitool\settings.json.</summary>
public sealed class AppSettings
{
    public double WarningTempC { get; set; } = 80;
    public double CriticalTempC { get; set; } = 90;
    public int DurationSeconds { get; set; } = 60;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}
