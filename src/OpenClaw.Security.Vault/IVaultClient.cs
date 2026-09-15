namespace OpenClaw.Security.Vault;

/// <summary>
/// Thin abstraction over VaultSharp for unit-test substitution. Implementations
/// must return the KV v2 data dictionary for <paramref name="path"/> under
/// <paramref name="mount"/>, or null when the path does not exist.
/// </summary>
public interface IVaultClient
{
    Task<Dictionary<string, object>?> ReadSecretV2Async(
        string mount, string path, CancellationToken ct);
}
