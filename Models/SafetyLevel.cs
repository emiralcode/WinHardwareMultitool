namespace WinHardwareMultitool.Models;

/// <summary>Ordered so that direct comparison (Warning &gt; Normal, Critical &gt; Warning) works.</summary>
public enum SafetyLevel
{
    Normal = 0,
    Warning = 1,
    Critical = 2
}
