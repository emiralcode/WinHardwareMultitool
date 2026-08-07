namespace WinHardwareMultitool.Models;

public sealed class CpuSnapshot
{
    public string Name { get; set; } = "N/A";
    public double? LoadPercent { get; set; }
    public double? TemperatureC { get; set; }
    public double? FanRpm { get; set; }
    public double? PowerW { get; set; }

    /// <summary>Fallback reading (e.g. the integrated GPU's SoC/VRM sensor on an APU) used only when
    /// <see cref="TemperatureC"/> is unavailable - same package, not the same measurement point.</summary>
    public double? ApproxSocTempC { get; set; }
}

public sealed class GpuSnapshot
{
    public string Name { get; set; } = "N/A";
    public double? LoadPercent { get; set; }
    public double? TemperatureC { get; set; }
    public double? FanRpm { get; set; }
    public double? FanPercent { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
}

public sealed class MemorySnapshot
{
    public double? TotalGb { get; set; }
    public double? UsedGb { get; set; }
    public double? AvailableGb { get; set; }
    public double? UsedPercent { get; set; }
}

public sealed class DiskSnapshot
{
    public string Name { get; set; } = "N/A";
    public double? ReadRateMBs { get; set; }
    public double? WriteRateMBs { get; set; }
    public double? TemperatureC { get; set; }
}

public sealed class HardwareSnapshot
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public CpuSnapshot Cpu { get; set; } = new();
    public GpuSnapshot Gpu { get; set; } = new();
    public MemorySnapshot Memory { get; set; } = new();
    public List<DiskSnapshot> Disks { get; set; } = new();
    public List<RawSensorReading> AllSensors { get; set; } = new();
}
