using System.Collections.Concurrent;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway.Pipeline;

internal sealed record StartupNoticeSnapshot(string Message, int Count);

internal sealed class StartupNoticeCollector : IStartupNoticeSink
{
    private readonly object _gate = new();
    private readonly List<StartupNoticeSnapshot> _ordered = [];
    private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);
    private TextWriter? _liveOutput;
    private DateTimeOffset _liveUntilUtc;
    private bool _liveHeaderWritten;

    public IReadOnlyList<StartupNoticeSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return [.. _ordered];
        }
    }

    public void EnableLiveOutput(TextWriter output, TimeSpan duration, bool headerAlreadyWritten)
    {
        lock (_gate)
        {
            _liveOutput = output;
            _liveUntilUtc = DateTimeOffset.UtcNow.Add(duration);
            _liveHeaderWritten = headerAlreadyWritten;
        }
    }

    public void Record(string message)
    {
        TextWriter? liveOutput = null;
        var writeHeader = false;
        var writeMessage = false;

        lock (_gate)
        {
            if (_indexes.TryGetValue(message, out var index))
            {
                var existing = _ordered[index];
                _ordered[index] = existing with { Count = existing.Count + 1 };
            }
            else
            {
                _indexes[message] = _ordered.Count;
                _ordered.Add(new StartupNoticeSnapshot(message, 1));

                if (_liveOutput is not null && DateTimeOffset.UtcNow <= _liveUntilUtc)
                {
                    liveOutput = _liveOutput;
                    writeHeader = !_liveHeaderWritten;
                    _liveHeaderWritten = true;
                    writeMessage = true;
                }
            }
        }

        if (liveOutput is not null && writeMessage)
        {
            if (writeHeader)
                liveOutput.WriteLine("Started with notices:");

            liveOutput.WriteLine($"- {message}");
            liveOutput.Flush();
        }
    }
}
