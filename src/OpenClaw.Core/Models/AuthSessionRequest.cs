namespace OpenClaw.Core.Models;

public sealed class AuthSessionRequest
{
    public bool Remember { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? AccountToken { get; init; }
}

