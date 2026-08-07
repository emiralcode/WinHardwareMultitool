using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.Services;

/// <summary>
/// Autonomous thermal safety layer. Evaluates every snapshot produced by
/// <see cref="HardwareMonitorService"/> - independent of whether a stress test is running -
/// and kills any registered stress test the instant CPU/GPU temperature reaches the
/// critical threshold. Also gates new test starts while critical.
/// </summary>
public sealed class SafetyGuardService : IDisposable
{
    private readonly HardwareMonitorService _monitor;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeTestCts;

    public SafetyThresholds Thresholds { get; } = new();

    public SafetyLevel CurrentLevel { get; private set; } = SafetyLevel.Normal;

    /// <summary>Fires whenever the overall safety level changes (Normal/Warning/Critical).</summary>
    public event EventHandler<SafetyEventArgs>? LevelChanged;

    /// <summary>Fires when the emergency abort procedure actually runs (edge-triggered, not once per tick).</summary>
    public event EventHandler<SafetyEventArgs>? CriticalTriggered;

    public event EventHandler<string>? LogMessage;

    public SafetyGuardService(HardwareMonitorService monitor)
    {
        _monitor = monitor;
        _monitor.SensorsUpdated += OnSensorsUpdated;
    }

    /// <summary>Called by StressTestService right before a test starts so Safety Guard can kill it.</summary>
    public void RegisterActiveTest(CancellationTokenSource cts)
    {
        lock (_sync) _activeTestCts = cts;
    }

    public void UnregisterActiveTest()
    {
        lock (_sync) _activeTestCts = null;
    }

    /// <summary>Blocks new test starts while temperatures are already critical, even if the user insists.</summary>
    public bool IsSafeToStart(out string? reason)
    {
        if (CurrentLevel == SafetyLevel.Critical)
        {
            reason = $"Sıcaklık kritik eşiğin ({Thresholds.CriticalTempC:0.#}°C) üzerinde. Test başlatılamaz.";
            return false;
        }

        reason = null;
        return true;
    }

    private void OnSensorsUpdated(object? sender, HardwareSnapshot snapshot) => Evaluate(snapshot);

    private void Evaluate(HardwareSnapshot snapshot)
    {
        var (worst, worstLabel, worstValue) = ComputeWorst(snapshot);

        bool levelChanged = worst != CurrentLevel;
        if (levelChanged)
        {
            CurrentLevel = worst;
            LevelChanged?.Invoke(this, new SafetyEventArgs(worst, worstLabel ?? "N/A", worstValue));
        }

        if (worst == SafetyLevel.Critical)
        {
            bool aborted = TryAbortActiveTest(worstLabel ?? "N/A", worstValue);
            if (levelChanged || aborted)
                CriticalTriggered?.Invoke(this, new SafetyEventArgs(SafetyLevel.Critical, worstLabel ?? "N/A", worstValue));
        }
    }

    private (SafetyLevel Level, string? Label, double Value) ComputeWorst(HardwareSnapshot snapshot)
    {
        var readings = new (string Label, double? Value)[]
        {
            ("CPU", snapshot.Cpu.TemperatureC),
            ("GPU", snapshot.Gpu.TemperatureC)
        };

        var worst = SafetyLevel.Normal;
        string? worstLabel = null;
        double worstValue = 0;

        foreach (var (label, value) in readings)
        {
            if (value is not { } v) continue;

            var level = v >= Thresholds.CriticalTempC ? SafetyLevel.Critical
                : v >= Thresholds.WarningTempC ? SafetyLevel.Warning
                : SafetyLevel.Normal;

            if (level > worst)
            {
                worst = level;
                worstLabel = label;
                worstValue = v;
            }
        }

        return (worst, worstLabel, worstValue);
    }

    private bool TryAbortActiveTest(string label, double value)
    {
        CancellationTokenSource? cts;
        lock (_sync) cts = _activeTestCts;

        if (cts is null)
            return false;

        try
        {
            if (cts.IsCancellationRequested)
                return false;

            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Test finished and disposed its token source in the moment between our read and Cancel() - nothing to abort.
            return false;
        }

        LogMessage?.Invoke(this,
            $"ACİL DURUM: {label} sıcaklığı {value:0.#}°C - kritik eşik ({Thresholds.CriticalTempC:0.#}°C) aşıldı. Test derhal iptal ediliyor.");
        return true;
    }

    public void Dispose()
    {
        _monitor.SensorsUpdated -= OnSensorsUpdated;
    }
}
