using System.IO;
using System.Text.Json;
using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.Services;

/// <summary>Best-effort JSON persistence for user preferences. Never throws - a missing or
/// corrupt settings file just falls back to defaults, since none of this is critical state.</summary>
public sealed class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinHardwareMultitool", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // corrupt or unreadable file - fall back to defaults below
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // non-critical - user just keeps default thresholds next run
        }
    }
}
