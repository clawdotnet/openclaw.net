using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Gateway;

internal static class JsonlQueryBuffer
{
    public static IReadOnlyList<T> ReadLatest<T>(
        string path,
        object gate,
        int limit,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<T, bool> predicate,
        ILogger logger,
        string parseFailureMessage)
    {
        if (!File.Exists(path))
            return [];

        var matches = new Queue<T>(Math.Max(limit, 1));

        lock (gate)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var item = JsonSerializer.Deserialize(line, jsonTypeInfo);
                    if (item is null || !predicate(item))
                        continue;

                    if (matches.Count == limit)
                        matches.Dequeue();

                    matches.Enqueue(item);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, parseFailureMessage, path);
                }
            }
        }

        return [.. matches.Reverse()];
    }
}
