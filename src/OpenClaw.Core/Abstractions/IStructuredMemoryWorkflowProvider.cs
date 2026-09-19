using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

/// <summary>Optional capture and resume operations supported by newer FractalMem servers.</summary>
public interface IStructuredMemoryWorkflowProvider
{
    Task<StructuredMemoryWorkflowResult> ExecuteWorkflowAsync(string operation, JsonElement arguments, CancellationToken ct);
    Task<StructuredMemoryExportResult> BuildContextAsync(string path, int maxCharacters, CancellationToken ct);
}
