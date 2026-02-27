using System.Collections.Concurrent;

namespace OpenClaw.Agent.Tools;

internal static class MqttMessageCache
{
    private sealed record CacheEntry(string Topic, string Payload, DateTimeOffset ReceivedAt);

    private static readonly ConcurrentDictionary<string, CacheEntry> LastByTopic = new(StringComparer.Ordinal);

    public static void Set(string topic, string payload)
        => LastByTopic[topic] = new CacheEntry(topic, payload, DateTimeOffset.UtcNow);

    public static (bool found, string? payload, DateTimeOffset? receivedAt) TryGet(string topic)
    {
        if (LastByTopic.TryGetValue(topic, out var entry))
            return (true, entry.Payload, entry.ReceivedAt);
        return (false, null, null);
    }

    public static IReadOnlyList<(string topic, string payload, DateTimeOffset receivedAt)> FindByGlob(string topicGlob)
    {
        var list = new List<(string topic, string payload, DateTimeOffset receivedAt)>();
        foreach (var (topic, entry) in LastByTopic)
        {
            if (OpenClaw.Core.Security.GlobMatcher.IsMatch(topicGlob, topic))
                list.Add((topic: topic, payload: entry.Payload, receivedAt: entry.ReceivedAt));
        }

        list.Sort((a, b) => b.receivedAt.CompareTo(a.receivedAt));
        return list;
    }
}
