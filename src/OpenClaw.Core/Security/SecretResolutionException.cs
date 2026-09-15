namespace OpenClaw.Core.Security;

/// <summary>
/// Thrown when a secret reference cannot be resolved. The legacy sync fallback
/// path may throw this before Vault types load; async paths and the vault
/// backend throw vault-specific exceptions derived from this base.
/// </summary>
public class SecretResolutionException : Exception
{
    public SecretResolutionException(string message) : base(message) { }
    public SecretResolutionException(string message, Exception inner) : base(message, inner) { }
}
