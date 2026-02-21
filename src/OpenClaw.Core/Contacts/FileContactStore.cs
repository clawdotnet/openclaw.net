using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Contacts;

public sealed class FileContactStore : IContactStore
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public FileContactStore(string basePath)
    {
        Directory.CreateDirectory(basePath);
        _path = Path.Combine(basePath, "contacts.json");
    }

    public ValueTask<Contact?> GetAsync(string phoneE164, CancellationToken ct)
    {
        lock (_lock)
        {
            var state = LoadUnsafe();
            state.ContactsByPhone.TryGetValue(phoneE164, out var contact);
            return ValueTask.FromResult(contact);
        }
    }

    public ValueTask<Contact> TouchAsync(string phoneE164, CancellationToken ct)
    {
        lock (_lock)
        {
            var state = LoadUnsafe();
            if (!state.ContactsByPhone.TryGetValue(phoneE164, out var contact))
            {
                contact = new Contact { PhoneE164 = phoneE164 };
                state.ContactsByPhone[phoneE164] = contact;
            }

            contact.LastSeenAt = DateTimeOffset.UtcNow;
            SaveUnsafe(state);
            return ValueTask.FromResult(contact);
        }
    }

    public ValueTask SetDoNotTextAsync(string phoneE164, bool doNotText, CancellationToken ct)
    {
        lock (_lock)
        {
            var state = LoadUnsafe();
            if (!state.ContactsByPhone.TryGetValue(phoneE164, out var contact))
            {
                contact = new Contact { PhoneE164 = phoneE164 };
                state.ContactsByPhone[phoneE164] = contact;
            }

            contact.DoNotText = doNotText;
            contact.LastSeenAt = DateTimeOffset.UtcNow;
            SaveUnsafe(state);
            return ValueTask.CompletedTask;
        }
    }

    private ContactStoreState LoadUnsafe()
    {
        if (!File.Exists(_path))
            return new ContactStoreState();

        using var stream = File.OpenRead(_path);
        return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.ContactStoreState)
            ?? new ContactStoreState();
    }

    private void SaveUnsafe(ContactStoreState state)
    {
        var tmp = _path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, state, CoreJsonContext.Default.ContactStoreState);
        }

        File.Move(tmp, _path, overwrite: true);
    }
}
