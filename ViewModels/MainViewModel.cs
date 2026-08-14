using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinHardwareMultitool.Models;
using WinHardwareMultitool.Services;

namespace WinHardwareMultitool.ViewModels;

public sealed record DurationOption(int Seconds, string Label);

public sealed class MainViewModel : ObservableObject
{
    private const int HistoryLength = 60;
    private const double SparkWidth = 120;
    private const double SparkHeight = 28;

    private static readonly SolidColorBrush NormalBrush = Freeze(0x2E, 0xCC, 0x71);
    private static readonly SolidColorBrush WarningBrush = Freeze(0xF1, 0xC4, 0x0F);
    private static readonly SolidColorBrush CriticalBrush = Freeze(0xE7, 0x4C, 0x3C);
    private static readonly SolidColorBrush NeutralBrush = Freeze(0x90, 0x90, 0x90);

    private readonly Dispatcher _dispatcher;
    private readonly HardwareMonitorService _monitor;
    private readonly SafetyGuardService _safetyGuard;
    private readonly StressTestService _stressTest;
    private readonly SettingsService _settingsService = new();
    private AppSettings _lastSavedSettings = new();

    private readonly Queue<double> _cpuTempHistory = new();
    private readonly Queue<double> _gpuTempHistory = new();

    private CancellationTokenSource? _userTestCts;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _monitor = new HardwareMonitorService();
        _safetyGuard = new SafetyGuardService(_monitor);
        _stressTest = new StressTestService(_safetyGuard);

        _monitor.SensorsUpdated += (_, snapshot) => RunOnUi(() => ApplySnapshot(snapshot));
        _monitor.SensorError += (_, msg) => EnqueueLog(msg);

        _safetyGuard.LevelChanged += (_, e) => RunOnUi(() => ApplySafetyLevel(e));
        _safetyGuard.CriticalTriggered += (_, e) => RunOnUi(() => RaiseEmergency(e));
        _safetyGuard.LogMessage += (_, msg) => EnqueueLog(msg);

        _stressTest.LogMessage += (_, msg) => EnqueueLog(msg);
        _stressTest.TestStarted += (_, _) => _testActuallyStarted = true;

        RawSensorsView = CollectionViewSource.GetDefaultView(RawSensors);
        RawSensorsView.Filter = FilterRawSensor;
        RawSensorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RawSensorReading.Hardware)));

        StartCpuTestCommand = new RelayCommand(param => FireAndForget(StartTestAsync(StressTestType.Cpu)), param => !IsTestRunning);
        StartGpuTestCommand = new RelayCommand(param => FireAndForget(StartTestAsync(StressTestType.Gpu)), param => !IsTestRunning);
        StartDiskTestCommand = new RelayCommand(param => FireAndForget(StartTestAsync(StressTestType.Disk)), param => !IsTestRunning);
        StopTestCommand = new RelayCommand(param => StopTest(), param => IsTestRunning);
        ExportLogCommand = new RelayCommand(param => ExportLogRequested?.Invoke(this, LogEntries.ToList()));

        _lastSavedSettings = _settingsService.Load();
        _warningTempC = _lastSavedSettings.WarningTempC;
        _criticalTempC = _lastSavedSettings.CriticalTempC;
        _safetyGuard.Thresholds.WarningTempC = _lastSavedSettings.WarningTempC;
        _safetyGuard.Thresholds.CriticalTempC = _lastSavedSettings.CriticalTempC;
        SelectedDuration = DurationOptions.FirstOrDefault(d => d.Seconds == _lastSavedSettings.DurationSeconds) ?? DurationOptions[1];
    }

    /// <summary>Window bounds saved from the previous run, if any - read once by MainWindow at startup.</summary>
    public (double? Width, double? Height, double? Left, double? Top) SavedWindowBounds =>
        (_lastSavedSettings.WindowWidth, _lastSavedSettings.WindowHeight, _lastSavedSettings.WindowLeft, _lastSavedSettings.WindowTop);

    /// <summary>Called by MainWindow just before it closes so the final window bounds get persisted
    /// alongside whatever thresholds/duration are already tracked here - keeps a single writer for
    /// settings.json instead of two independent Save() calls racing to overwrite each other.</summary>
    public void SaveWindowBounds(double width, double height, double left, double top)
    {
        _lastSavedSettings.WindowWidth = width;
        _lastSavedSettings.WindowHeight = height;
        _lastSavedSettings.WindowLeft = left;
        _lastSavedSettings.WindowTop = top;
        PersistSettings();
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    public void Initialize()
    {
        _monitor.Start();
        EnqueueLog("Donanım izleme başlatıldı.");
    }

    public async Task ShutdownAsync()
    {
        _userTestCts?.Cancel();
        await _monitor.StopAsync().ConfigureAwait(false);
        _safetyGuard.Dispose();
        _monitor.Dispose();
    }

    // -------------------------------------------------------------- CPU

    private string _cpuName = "N/A";
    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }

    private string _cpuLoadText = "N/A";
    public string CpuLoadText { get => _cpuLoadText; private set => SetProperty(ref _cpuLoadText, value); }

    private double _cpuLoadValue;
    public double CpuLoadValue { get => _cpuLoadValue; private set => SetProperty(ref _cpuLoadValue, value); }

    private string _cpuTempText = "N/A";
    public string CpuTempText { get => _cpuTempText; private set => SetProperty(ref _cpuTempText, value); }

    private PointCollection _cpuTempSpark = new();
    public PointCollection CpuTempSpark { get => _cpuTempSpark; private set => SetProperty(ref _cpuTempSpark, value); }

    private string _cpuFanText = "N/A";
    public string CpuFanText { get => _cpuFanText; private set => SetProperty(ref _cpuFanText, value); }

    private string _cpuPowerText = "N/A";
    public string CpuPowerText { get => _cpuPowerText; private set => SetProperty(ref _cpuPowerText, value); }

    private string _cpuApproxSocText = "N/A";
    public string CpuApproxSocText { get => _cpuApproxSocText; private set => SetProperty(ref _cpuApproxSocText, value); }

    private SolidColorBrush _cpuTempBrush = NeutralBrush;
    public SolidColorBrush CpuTempBrush { get => _cpuTempBrush; private set => SetProperty(ref _cpuTempBrush, value); }

    // -------------------------------------------------------------- GPU

    private string _gpuName = "N/A";
    public string GpuName { get => _gpuName; private set => SetProperty(ref _gpuName, value); }

    private string _gpuLoadText = "N/A";
    public string GpuLoadText { get => _gpuLoadText; private set => SetProperty(ref _gpuLoadText, value); }

    private double _gpuLoadValue;
    public double GpuLoadValue { get => _gpuLoadValue; private set => SetProperty(ref _gpuLoadValue, value); }

    private string _gpuTempText = "N/A";
    public string GpuTempText { get => _gpuTempText; private set => SetProperty(ref _gpuTempText, value); }

    private PointCollection _gpuTempSpark = new();
    public PointCollection GpuTempSpark { get => _gpuTempSpark; private set => SetProperty(ref _gpuTempSpark, value); }

    private string _gpuFanText = "N/A";
    public string GpuFanText { get => _gpuFanText; private set => SetProperty(ref _gpuFanText, value); }

    private string _gpuVramText = "N/A";
    public string GpuVramText { get => _gpuVramText; private set => SetProperty(ref _gpuVramText, value); }

    private SolidColorBrush _gpuTempBrush = NeutralBrush;
    public SolidColorBrush GpuTempBrush { get => _gpuTempBrush; private set => SetProperty(ref _gpuTempBrush, value); }

    // -------------------------------------------------------------- RAM

    private string _memUsedText = "N/A";
    public string MemUsedText { get => _memUsedText; private set => SetProperty(ref _memUsedText, value); }

    private string _memTotalText = "N/A";
    public string MemTotalText { get => _memTotalText; private set => SetProperty(ref _memTotalText, value); }

    private string _memPercentText = "N/A";
    public string MemPercentText { get => _memPercentText; private set => SetProperty(ref _memPercentText, value); }

    private double _memPercentValue;
    public double MemPercentValue { get => _memPercentValue; private set => SetProperty(ref _memPercentValue, value); }

    // -------------------------------------------------------------- Disk

    public ObservableCollection<DiskRowViewModel> Disks { get; } = new();

    // -------------------------------------------------------------- Diagnostics

    public ObservableCollection<RawSensorReading> RawSensors { get; } = new();

    /// <summary>Grouped-by-hardware, filterable view over RawSensors for the diagnostics panel.</summary>
    public ICollectionView RawSensorsView { get; }

    private string _rawSensorFilter = string.Empty;
    public string RawSensorFilter
    {
        get => _rawSensorFilter;
        set
        {
            if (SetProperty(ref _rawSensorFilter, value))
                RawSensorsView.Refresh();
        }
    }

    private bool FilterRawSensor(object obj)
    {
        if (string.IsNullOrWhiteSpace(RawSensorFilter)) return true;
        if (obj is not RawSensorReading r) return false;

        string needle = RawSensorFilter;
        return r.Hardware.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.Sensor.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.Type.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || r.ValueText.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- Safety

    private string _safetyLevelText = "Normal";
    public string SafetyLevelText { get => _safetyLevelText; private set => SetProperty(ref _safetyLevelText, value); }

    private SolidColorBrush _safetyLevelBrush = NormalBrush;
    public SolidColorBrush SafetyLevelBrush { get => _safetyLevelBrush; private set => SetProperty(ref _safetyLevelBrush, value); }

    private string _emergencyMessage = string.Empty;
    public string EmergencyMessage { get => _emergencyMessage; private set => SetProperty(ref _emergencyMessage, value); }

    public event EventHandler? EmergencyRaised;

    private double _warningTempC = 80;
    public double WarningTempC
    {
        get => _warningTempC;
        set
        {
            if (SetProperty(ref _warningTempC, value))
            {
                _safetyGuard.Thresholds.WarningTempC = value;
                EnqueueLog($"Uyarı eşiği {value:0.#}°C olarak güncellendi.");
                PersistSettings();
            }
        }
    }

    private double _criticalTempC = 90;
    public double CriticalTempC
    {
        get => _criticalTempC;
        set
        {
            if (SetProperty(ref _criticalTempC, value))
            {
                _safetyGuard.Thresholds.CriticalTempC = value;
                EnqueueLog($"Kritik eşik {value:0.#}°C olarak güncellendi.");
                PersistSettings();
            }
        }
    }

    private void PersistSettings()
    {
        _lastSavedSettings.WarningTempC = WarningTempC;
        _lastSavedSettings.CriticalTempC = CriticalTempC;
        _lastSavedSettings.DurationSeconds = SelectedDuration?.Seconds ?? 60;
        _settingsService.Save(_lastSavedSettings);
    }

    // -------------------------------------------------------------- Tests

    public List<DurationOption> DurationOptions { get; } = new()
    {
        new DurationOption(30, "30 saniye"),
        new DurationOption(60, "60 saniye"),
        new DurationOption(120, "120 saniye"),
        new DurationOption(0, "Süresiz (Manuel Durdur)")
    };

    private DurationOption _selectedDuration = null!;
    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set
        {
            if (SetProperty(ref _selectedDuration, value))
                PersistSettings();
        }
    }

    private bool _isTestRunning;
    public bool IsTestRunning
    {
        get => _isTestRunning;
        private set
        {
            if (SetProperty(ref _isTestRunning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _activeTestLabel = "-";
    public string ActiveTestLabel { get => _activeTestLabel; private set => SetProperty(ref _activeTestLabel, value); }

    public ICommand StartCpuTestCommand { get; }
    public ICommand StartGpuTestCommand { get; }
    public ICommand StartDiskTestCommand { get; }
    public ICommand StopTestCommand { get; }
    public ICommand ExportLogCommand { get; }

    /// <summary>Raised when the user asks to export the log; the View owns the save-file dialog and disk write.</summary>
    public event EventHandler<IReadOnlyList<string>>? ExportLogRequested;

    private static void FireAndForget(Task task) { }

    private StressTestType? _activeTestType;
    private bool _testActuallyStarted;
    private DateTime _testStartedAt;
    private double? _testPeakTempC;
    private double _testLoadSum;
    private int _testLoadSamples;

    private async Task StartTestAsync(StressTestType type)
    {
        if (IsTestRunning) return;

        _userTestCts = new CancellationTokenSource();
        IsTestRunning = true;
        ActiveTestLabel = type.ToString();

        _activeTestType = type;
        _testActuallyStarted = false;
        _testStartedAt = DateTime.Now;
        _testPeakTempC = null;
        _testLoadSum = 0;
        _testLoadSamples = 0;

        TimeSpan? duration = SelectedDuration.Seconds > 0 ? TimeSpan.FromSeconds(SelectedDuration.Seconds) : null;

        await _stressTest.RunAsync(type, duration, _userTestCts.Token);

        if (_testActuallyStarted)
            LogTestSummary(type);
        _activeTestType = null;

        _userTestCts?.Dispose();
        _userTestCts = null;
        IsTestRunning = false;
        ActiveTestLabel = "-";
    }

    /// <summary>Reports what actually happened during the just-finished test - requested so the
    /// log isn't just "started/completed" but shows peak temperature and average load reached.</summary>
    private void LogTestSummary(StressTestType type)
    {
        var elapsed = DateTime.Now - _testStartedAt;

        if (type == StressTestType.Disk)
        {
            EnqueueLog($"Disk test özeti: süre {elapsed.TotalSeconds:0} sn (hız detayları yukarıda).");
            return;
        }

        string peak = _testPeakTempC is { } p ? $"{p:0.#}°C" : "N/A";
        string avgLoad = _testLoadSamples > 0 ? $"{_testLoadSum / _testLoadSamples:0.#}%" : "N/A";
        EnqueueLog($"{type} test özeti: süre {elapsed.TotalSeconds:0} sn, zirve sıcaklık {peak}, ortalama yük {avgLoad}.");
    }

    private void TrackActiveTestStats(HardwareSnapshot s)
    {
        double? temp = _activeTestType switch
        {
            StressTestType.Cpu => s.Cpu.TemperatureC,
            StressTestType.Gpu => s.Gpu.TemperatureC,
            _ => null
        };
        double? load = _activeTestType switch
        {
            StressTestType.Cpu => s.Cpu.LoadPercent,
            StressTestType.Gpu => s.Gpu.LoadPercent,
            _ => null
        };

        if (temp is { } t && (_testPeakTempC is null || t > _testPeakTempC))
            _testPeakTempC = t;

        if (load is { } l)
        {
            _testLoadSum += l;
            _testLoadSamples++;
        }
    }

    private void StopTest()
    {
        if (_userTestCts is null) return;
        EnqueueLog("Test kullanıcı tarafından durduruluyor...");
        _userTestCts.Cancel();
    }

    // -------------------------------------------------------------- Log

    public ObservableCollection<string> LogEntries { get; } = new();

    private void EnqueueLog(string message)
    {
        RunOnUi(() =>
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);
        });
    }

    // -------------------------------------------------------------- Snapshot application

    private void ApplySnapshot(HardwareSnapshot s)
    {
        TrackActiveTestStats(s);

        CpuName = s.Cpu.Name;
        CpuLoadText = FormatPercent(s.Cpu.LoadPercent);
        CpuLoadValue = s.Cpu.LoadPercent ?? 0;
        CpuTempText = FormatTemp(s.Cpu.TemperatureC);
        CpuFanText = FormatRpm(s.Cpu.FanRpm);
        CpuPowerText = s.Cpu.PowerW is { } pw ? $"{pw:0.#} W" : "N/A";
        CpuApproxSocText = s.Cpu.ApproxSocTempC is { } soc ? $"~{soc:0.#} °C" : "N/A";
        CpuTempBrush = BrushForTemp(s.Cpu.TemperatureC);
        CpuTempSpark = PushHistory(_cpuTempHistory, s.Cpu.TemperatureC);

        GpuName = s.Gpu.Name;
        GpuLoadText = FormatPercent(s.Gpu.LoadPercent);
        GpuLoadValue = s.Gpu.LoadPercent ?? 0;
        GpuTempText = FormatTemp(s.Gpu.TemperatureC);
        GpuFanText = s.Gpu.FanRpm is { } rpm ? $"{rpm:0} RPM" : s.Gpu.FanPercent is { } fp ? $"{fp:0.#} %" : "N/A";
        GpuVramText = s.Gpu.VramUsedMb is { } used && s.Gpu.VramTotalMb is { } total && total > 0
            ? $"{used:0} / {total:0} MB ({used / total * 100.0:0.#}%)"
            : "N/A";
        GpuTempBrush = BrushForTemp(s.Gpu.TemperatureC);
        GpuTempSpark = PushHistory(_gpuTempHistory, s.Gpu.TemperatureC);

        MemUsedText = FormatGb(s.Memory.UsedGb);
        MemTotalText = FormatGb(s.Memory.TotalGb);
        MemPercentText = s.Memory.UsedPercent is { } mp ? $"{mp:0.#} %" : "N/A";
        MemPercentValue = s.Memory.UsedPercent ?? 0;

        Disks.Clear();
        foreach (var disk in s.Disks)
            Disks.Add(new DiskRowViewModel(disk));

        RawSensors.Clear();
        foreach (var sensor in s.AllSensors)
            RawSensors.Add(sensor);
    }

    /// <summary>Appends a reading to a rolling history buffer and rebuilds the sparkline's point set.
    /// Missing readings (null) are skipped rather than plotted as a false drop to zero.</summary>
    private static PointCollection PushHistory(Queue<double> history, double? value)
    {
        if (value is { } v)
        {
            history.Enqueue(v);
            while (history.Count > HistoryLength)
                history.Dequeue();
        }

        if (history.Count < 2)
            return new PointCollection();

        var points = new PointCollection();
        double stepX = SparkWidth / (HistoryLength - 1);
        int startIndex = HistoryLength - history.Count;
        int i = 0;
        foreach (var sample in history)
        {
            double x = (startIndex + i) * stepX;
            double y = SparkHeight - Math.Clamp(sample, 0, 100) / 100.0 * SparkHeight;
            points.Add(new Point(x, y));
            i++;
        }
        return points;
    }

    private void ApplySafetyLevel(SafetyEventArgs e)
    {
        SafetyLevelText = e.Level switch
        {
            SafetyLevel.Critical => $"KRİTİK: {e.Source} {e.Value:0.#}°C",
            SafetyLevel.Warning => $"UYARI: {e.Source} {e.Value:0.#}°C",
            _ => "Normal"
        };

        SafetyLevelBrush = e.Level switch
        {
            SafetyLevel.Critical => CriticalBrush,
            SafetyLevel.Warning => WarningBrush,
            _ => NormalBrush
        };

        // Spec calls for an audible cue on Warning as well as Critical - this only fires on the
        // edge transition into a level (see SafetyGuardService.Evaluate), not on every tick.
        switch (e.Level)
        {
            case SafetyLevel.Warning:
                SystemSounds.Exclamation.Play();
                break;
            case SafetyLevel.Critical:
                SystemSounds.Hand.Play();
                break;
        }
    }

    private void RaiseEmergency(SafetyEventArgs e)
    {
        EmergencyMessage =
            $"ACİL DURUM: {e.Source} sıcaklığı {e.Value:0.#}°C - kritik eşik ({_safetyGuard.Thresholds.CriticalTempC:0.#}°C) aşıldı!\n" +
            "Çalışan tüm stres testleri durduruldu.";
        EmergencyRaised?.Invoke(this, EventArgs.Empty);
    }

    private SolidColorBrush BrushForTemp(double? temp)
    {
        if (temp is not { } t) return NeutralBrush;
        if (t >= _safetyGuard.Thresholds.CriticalTempC) return CriticalBrush;
        if (t >= _safetyGuard.Thresholds.WarningTempC) return WarningBrush;
        return NormalBrush;
    }

    private static string FormatPercent(double? v) => v is { } x ? $"{x:0.#} %" : "N/A";
    private static string FormatTemp(double? v) => v is { } x ? $"{x:0.#} °C" : "N/A";
    private static string FormatRpm(double? v) => v is { } x ? $"{x:0} RPM" : "N/A";
    private static string FormatGb(double? v) => v is { } x ? $"{x:0.##} GB" : "N/A";
}
