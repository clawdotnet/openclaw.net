using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenClaw.Gateway;

internal static class AtomicJsonFileStore
{
    public static bool TryLoad<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        out T? value,
        out string? error)
    {
        value = default;
        error = null;

        try
        {
            if (!File.Exists(path))
                return true;

            var json = File.ReadAllText(path);
            value = JsonSerializer.Deserialize(json, jsonTypeInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryWriteAtomic<T>(
        string path,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        out string? error)
    {
        error = null;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(value, jsonTypeInfo);
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryAppendLine(
        string path,
        string line,
        object gate,
        out string? error)
    {
        error = null;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
