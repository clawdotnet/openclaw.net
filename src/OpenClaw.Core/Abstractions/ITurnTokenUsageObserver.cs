using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ITurnTokenUsageObserver
{
    void RecordTurn(TurnTokenUsageRecord record);
}