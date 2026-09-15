using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public sealed class VaultRefParseException : SecretResolutionException
{
    public VaultRefParseException(string message) : base(message) { }
}

public sealed class VaultAuthException : SecretResolutionException
{
    public VaultAuthException(string message) : base(message) { }
}

public sealed class VaultUnavailableException : SecretResolutionException
{
    public bool Retryable { get; }
    public VaultUnavailableException(string message, bool retryable = true) : base(message)
    {
        Retryable = retryable;
    }
}

public sealed class VaultPathNotFoundException : SecretResolutionException
{
    public VaultPathNotFoundException(string message) : base(message) { }
}

public sealed class VaultKeyNotFoundException : SecretResolutionException
{
    public VaultKeyNotFoundException(string message) : base(message) { }
}
