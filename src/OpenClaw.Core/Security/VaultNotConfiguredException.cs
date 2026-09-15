namespace OpenClaw.Core.Security;

/// <summary>
/// Thrown when a reference uses the <c>vault:</c> scheme but no provider is
/// registered to resolve it (the Vault backend is disabled or not wired).
/// </summary>
public sealed class VaultNotConfiguredException : SecretResolutionException
{
    public VaultNotConfiguredException(string message) : base(message) { }
}
