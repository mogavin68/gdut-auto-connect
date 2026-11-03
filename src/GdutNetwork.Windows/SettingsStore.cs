using System.Text.Json;
using GdutNetwork.Core;

namespace GdutNetwork.Windows;

public sealed class SettingsStore(string? isolatedDirectory = null) : ISettingsStore
{
    public string DirectoryPath { get; } = isolatedDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GDUTAutoNetwork");
    public AutomationSettings Load()
    {
        string path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(path), DiagnosticService.JsonOptions);
            if (value is null || value.Version != 1 || !Enum.IsDefined(value.Pause) || value.Enabled && value.Pause != PauseReason.None)
                return new() { Pause = PauseReason.Configuration };
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return new() { Pause = PauseReason.Configuration }; }
    }
    public void Save(AutomationSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "settings.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, DiagnosticService.JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
