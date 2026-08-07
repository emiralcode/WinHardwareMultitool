using LibreHardwareMonitor.Hardware;
using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.Services;

/// <summary>
/// Owns the single LibreHardwareMonitor <see cref="Computer"/> instance and polls it on
/// a dedicated loop. All hardware.Update() calls happen from this one loop only - LHM's
/// driver-backed sensors are not guaranteed safe under concurrent Update() calls, so no
/// other component should read from the underlying Computer object directly.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    /// <summary>Default 1000ms per spec. Lower this for faster Safety Guard reaction time at the cost of overhead.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(1000);

    public event EventHandler<HardwareSnapshot>? SensorsUpdated;
    public event EventHandler<string>? SensorError;

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };
    }

    public void Start()
    {
        if (_pollingTask is not null) return;

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            SensorError?.Invoke(this, $"Donanım izleyici başlatılamadı (Yönetici olarak çalıştırdığınızdan emin olun): {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        try
        {
            if (_pollingTask is not null)
                await _pollingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _pollingTask = null;

            try { _computer.Close(); }
            catch { /* best effort */ }
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollingInterval);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = ReadSnapshot();
                SensorsUpdated?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                SensorError?.Invoke(this, $"Sensör okuma hatası: {ex.Message}");
            }

            try
            {
                await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private HardwareSnapshot ReadSnapshot()
    {
        _computer.Accept(_visitor);

        var snapshot = new HardwareSnapshot();
        int gpuPriority = -1;
        IHardware? amdIntegratedGpu = null;

        foreach (var hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    FillCpu(hardware, snapshot.Cpu);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    int priority = GpuPriority(hardware.HardwareType);
                    if (priority > gpuPriority)
                    {
                        gpuPriority = priority;
                        FillGpu(hardware, snapshot.Gpu);
                    }
                    if (hardware.HardwareType == HardwareType.GpuAmd)
                        amdIntegratedGpu = hardware;
                    break;

                case HardwareType.Memory:
                    FillMemory(hardware, snapshot.Memory);
                    break;

                case HardwareType.Storage:
                    snapshot.Disks.Add(FillDisk(hardware));
                    break;
            }

            CollectRawSensors(hardware, snapshot.AllSensors);
        }

        // On AMD APUs where a discrete GPU wins the main GPU card, the integrated GPU's SoC/VRM
        // temperature sensor is still the closest available proxy for CPU package heat when the
        // CPU's own temperature sensor can't be read (see FillCpu).
        if (snapshot.Cpu.TemperatureC is null && amdIntegratedGpu is not null)
        {
            snapshot.Cpu.ApproxSocTempC = PositiveOrNull(
                FindSensor(amdIntegratedGpu, SensorType.Temperature, "SoC")?.Value);
        }

        return snapshot;
    }

    /// <summary>Recursively dumps every sensor on every hardware node - used by the "Gelişmiş" diagnostics panel
    /// so a user can see raw readings even for sensors the curated CPU/GPU/RAM/Disk mapping above doesn't surface.</summary>
    private static void CollectRawSensors(IHardware hardware, List<RawSensorReading> into)
    {
        foreach (var sensor in hardware.Sensors)
        {
            into.Add(new RawSensorReading(hardware.Name, sensor.Name, sensor.SensorType.ToString(),
                sensor.Value is { } v ? $"{v:0.##} {UnitFor(sensor.SensorType)}" : "N/A"));
        }

        foreach (var sub in hardware.SubHardware)
            CollectRawSensors(sub, into);
    }

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Power => "W",
        SensorType.Fan => "RPM",
        SensorType.Voltage => "V",
        SensorType.Clock => "MHz",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Control => "%",
        _ => ""
    };

    private static int GpuPriority(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 3,
        HardwareType.GpuAmd => 2,
        HardwareType.GpuIntel => 1,
        _ => 0
    };

    private static void FillCpu(IHardware hardware, CpuSnapshot cpu)
    {
        cpu.Name = hardware.Name;

        cpu.LoadPercent = FindSensor(hardware, SensorType.Load, "Total")?.Value
                           ?? FindFirst(hardware, SensorType.Load)?.Value;

        // Intel exposes "CPU Package"; AMD exposes "Core (Tdie)" / "Core (Tctl)" instead - no "Package" temperature sensor.
        // Some chips (notably newer AMD mobile parts) expose the sensor but LHM can't read the real MSR and
        // reports a stub 0 instead of leaving it null - PositiveOrNull() treats that as "not available" and
        // falls through to the next candidate instead of showing a misleading "0 °C".
        cpu.TemperatureC = PositiveOrNull(FindSensor(hardware, SensorType.Temperature, "Package")?.Value)
                            ?? PositiveOrNull(FindSensor(hardware, SensorType.Temperature, "Tdie")?.Value)
                            ?? PositiveOrNull(FindSensor(hardware, SensorType.Temperature, "Tctl")?.Value)
                            ?? PositiveOrNull(HottestOf(hardware, SensorType.Temperature)?.Value);

        cpu.FanRpm = FindFirst(hardware, SensorType.Fan)?.Value;

        cpu.PowerW = PositiveOrNull(FindSensor(hardware, SensorType.Power, "Package")?.Value)
                     ?? PositiveOrNull(HottestOf(hardware, SensorType.Power)?.Value);
    }

    private static void FillGpu(IHardware hardware, GpuSnapshot gpu)
    {
        gpu.Name = hardware.Name;

        gpu.LoadPercent = FindSensor(hardware, SensorType.Load, "Core")?.Value
                           ?? FindFirst(hardware, SensorType.Load)?.Value;

        gpu.TemperatureC = PositiveOrNull(FindSensor(hardware, SensorType.Temperature, "Core")?.Value)
                            ?? PositiveOrNull(HottestOf(hardware, SensorType.Temperature)?.Value);

        gpu.FanRpm = FindFirst(hardware, SensorType.Fan)?.Value;
        gpu.FanPercent = FindSensor(hardware, SensorType.Control, "Fan")?.Value;

        // "GPU " prefix disambiguates from the "D3D Dedicated/Shared Memory Used" sensors on the same hardware.
        gpu.VramUsedMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Used")?.Value;
        gpu.VramTotalMb = FindSensor(hardware, SensorType.SmallData, "GPU Memory Total")?.Value;
    }

    private static void FillMemory(IHardware hardware, MemorySnapshot memory)
    {
        // Exact match: LHM also exposes "Virtual Memory Used/Available" sensors on the same hardware,
        // which a substring match against "Memory Used" would incorrectly pick up.
        memory.UsedGb = FindSensorExact(hardware, SensorType.Data, "Memory Used")?.Value;
        memory.AvailableGb = FindSensorExact(hardware, SensorType.Data, "Memory Available")?.Value;
        memory.UsedPercent = FindSensorExact(hardware, SensorType.Load, "Memory")?.Value;

        memory.TotalGb = memory.UsedGb is { } used && memory.AvailableGb is { } avail
            ? used + avail
            : null;
    }

    private static DiskSnapshot FillDisk(IHardware hardware)
    {
        double? BytesPerSecToMBs(double? bytesPerSec) => bytesPerSec is { } b ? b / 1_000_000.0 : null;

        return new DiskSnapshot
        {
            Name = hardware.Name,
            ReadRateMBs = BytesPerSecToMBs(FindSensor(hardware, SensorType.Throughput, "Read Rate")?.Value),
            WriteRateMBs = BytesPerSecToMBs(FindSensor(hardware, SensorType.Throughput, "Write Rate")?.Value),
            TemperatureC = PositiveOrNull(FindFirst(hardware, SensorType.Temperature)?.Value)
        };
    }

    /// <summary>Temperature/power sensors can never legitimately read exactly 0 while a machine is running -
    /// LHM uses that as a stub value when it can't get real hardware access. Treat it as "no data" instead.</summary>
    private static double? PositiveOrNull(double? value) => value is { } v && v > 0 ? v : null;

    private static SensorReading? FindSensor(IHardware hardware, SensorType type, string nameContains)
    {
        var sensor = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        return sensor is null ? null : new SensorReading(sensor.Value);
    }

    private static SensorReading? FindSensorExact(IHardware hardware, SensorType type, string exactName)
    {
        var sensor = hardware.Sensors.FirstOrDefault(s =>
            s.SensorType == type && string.Equals(s.Name, exactName, StringComparison.OrdinalIgnoreCase));
        return sensor is null ? null : new SensorReading(sensor.Value);
    }

    private static SensorReading? FindFirst(IHardware hardware, SensorType type)
    {
        var sensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == type);
        return sensor is null ? null : new SensorReading(sensor.Value);
    }

    private static SensorReading? HottestOf(IHardware hardware, SensorType type)
    {
        var sensor = hardware.Sensors
            .Where(s => s.SensorType == type && s.Value is > 0)
            .OrderByDescending(s => s.Value)
            .FirstOrDefault();
        return sensor is null ? null : new SensorReading(sensor.Value);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _computer.Close(); } catch { /* ignore */ }
        _cts?.Dispose();
    }

    /// <summary>Thin wrapper converting LHM's float? sensor value to double? for the rest of the app.</summary>
    private readonly struct SensorReading
    {
        public SensorReading(float? value) => Value = value;
        public double? Value { get; }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
