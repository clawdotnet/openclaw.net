using System.Text.Json;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FileContactStoreTests
{
    [Fact]
    public async Task TouchAsync_ConcurrentCalls_PersistAllContacts()
    {
        var root = CreateTempDir();
        var store = new FileContactStore(root);
        var numbers = Enumerable.Range(0, 50).Select(i => $"+1555000{i:D4}").ToArray();

        await Parallel.ForEachAsync(numbers, async (phone, ct) =>
        {
            await store.TouchAsync(phone, ct);
        });

        var contactsPath = Path.Combine(root, "contacts.json");
        await using var stream = File.OpenRead(contactsPath);
        var state = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.ContactStoreState, CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(numbers.Length, state!.ContactsByPhone.Count);
        foreach (var number in numbers)
            Assert.True(state.ContactsByPhone.ContainsKey(number));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
