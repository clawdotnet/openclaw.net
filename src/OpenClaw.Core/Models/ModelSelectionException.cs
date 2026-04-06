namespace OpenClaw.Core.Models;

public sealed class ModelSelectionException : InvalidOperationException
{
    public ModelSelectionException(string message)
        : base(message)
    {
    }
}
