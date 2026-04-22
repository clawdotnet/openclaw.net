namespace OpenClaw.Core.Observability;

public interface IStartupNoticeSink
{
    void Record(string message);
}

public sealed class NullStartupNoticeSink : IStartupNoticeSink
{
    public static NullStartupNoticeSink Instance { get; } = new();

    private NullStartupNoticeSink()
    {
    }

    public void Record(string message)
    {
    }
}
