using System.Text.Json;
using OpenClaw.Companion.Models;

namespace OpenClaw.Companion.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public SettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClaw",
            "Companion");
        SettingsPath = Path.Combine(dir, "settings.json");
    }

    public CompanionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new CompanionSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<CompanionSettings>(json, JsonOptions) ?? new CompanionSettings();
        }
        catch
        {
            return new CompanionSettings();
        }
    }

    public void Save(CompanionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        var toSave = new CompanionSettings
        {
            ServerUrl = settings.ServerUrl,
            RememberToken = settings.RememberToken,
            AuthToken = settings.RememberToken ? settings.AuthToken : null
        };

        var json = JsonSerializer.Serialize(toSave, JsonOptions);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}

