using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Pipeline;
using Xunit;

namespace OpenClaw.Tests;

public sealed class RecentSendersStoreTests
{
    [Fact]
    public async Task RecordAsync_TracksLatestPerChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new RecentSendersStore(root, NullLogger<RecentSendersStore>.Instance, maxEntries: 10);

        await store.RecordAsync("telegram", "1", "Alice", TestContext.Current.CancellationToken);
        await store.RecordAsync("telegram", "2", null, TestContext.Current.CancellationToken);

        var latest = store.TryGetLatest("telegram");
        Assert.NotNull(latest);
        Assert.Equal("2", latest!.SenderId);

        await store.RecordAsync("telegram", "1", "Alice", TestContext.Current.CancellationToken);
        latest = store.TryGetLatest("telegram");
        Assert.NotNull(latest);
        Assert.Equal("1", latest!.SenderId);
        Assert.Equal("Alice", latest.SenderName);
    }
}

