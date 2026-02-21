namespace OpenClaw.Core.Contacts;

public interface IContactStore
{
    ValueTask<Contact> TouchAsync(string phoneE164, CancellationToken ct);
    ValueTask<Contact?> GetAsync(string phoneE164, CancellationToken ct);
    ValueTask SetDoNotTextAsync(string phoneE164, bool doNotText, CancellationToken ct);
}

