namespace WinHardwareMultitool.Models;

public sealed class SafetyEventArgs : EventArgs
{
    public SafetyEventArgs(SafetyLevel level, string source, double value)
    {
        Level = level;
        Source = source;
        Value = value;
    }

    public SafetyLevel Level { get; }
    public string Source { get; }
    public double Value { get; }
}
