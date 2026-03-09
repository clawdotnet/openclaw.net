namespace OpenClaw.Core.Models;

public sealed class OperationStatusResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? Mode { get; set; }
}