using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.ViewModels;

public sealed class DiskRowViewModel
{
    public DiskRowViewModel(DiskSnapshot snapshot)
    {
        Name = snapshot.Name;
        ReadText = snapshot.ReadRateMBs is { } r ? $"{r:0.#} MB/s" : "N/A";
        WriteText = snapshot.WriteRateMBs is { } w ? $"{w:0.#} MB/s" : "N/A";
        TempText = snapshot.TemperatureC is { } t ? $"{t:0.#} °C" : "N/A";
    }

    public string Name { get; }
    public string ReadText { get; }
    public string WriteText { get; }
    public string TempText { get; }
}
