namespace OpenClaw.Gateway.A2A;

/// <summary>
/// The signed-in account behind the current A2A request. The A2A middleware sets it before the SDK runs the
/// handler; unlike the HTTP context, an async-local value still flows into work the SDK detaches from the request.
/// </summary>
internal static class A2ACallerContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? AccountId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
