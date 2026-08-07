namespace WinHardwareMultitool.Models;

/// <summary>Unfiltered sensor dump for the diagnostics panel - lets a user see every sensor LHM
/// exposes even when the curated CPU/GPU/RAM/Disk cards can't map it to something meaningful.</summary>
public sealed record RawSensorReading(string Hardware, string Sensor, string Type, string ValueText);
