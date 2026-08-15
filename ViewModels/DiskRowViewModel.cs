using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.ViewModels;

public sealed class DiskRowViewModel : ObservableObject
{
    public DiskRowViewModel(DiskSnapshot snapshot)
    {
        Name = snapshot.Name;
        Apply(snapshot);
    }

    public string Name { get; }

    private string _readText = "N/A";
    public string ReadText { get => _readText; private set => SetProperty(ref _readText, value); }

    private string _writeText = "N/A";
    public string WriteText { get => _writeText; private set => SetProperty(ref _writeText, value); }

    private string _tempText = "N/A";
    public string TempText { get => _tempText; private set => SetProperty(ref _tempText, value); }

    /// <summary>Updates this row's values in place so the Disks collection never has to be
    /// Clear()+Add()'d every tick just to refresh numbers on the same physical disk.</summary>
    public void Apply(DiskSnapshot snapshot)
    {
        ReadText = snapshot.ReadRateMBs is { } r ? $"{r:0.#} MB/s" : "N/A";
        WriteText = snapshot.WriteRateMBs is { } w ? $"{w:0.#} MB/s" : "N/A";
        TempText = snapshot.TemperatureC is { } t ? $"{t:0.#} °C" : "N/A";
    }
}
