using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

public sealed class SetupVerificationSnapshotStore
{
    private readonly string _path;

    public SetupVerificationSnapshotStore(string storagePath)
    {
        var rootedStorage = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStorage, "admin", "setup-verification.json");
    }

    public SetupVerificationSnapshot? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(_path), CoreJsonContext.Default.SetupVerificationSnapshot);
        }
        catch
        {
            return null;
        }
    }

    public bool Save(SetupVerificationSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.SetupVerificationSnapshot);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
