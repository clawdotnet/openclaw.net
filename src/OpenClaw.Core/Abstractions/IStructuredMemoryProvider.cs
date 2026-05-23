using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IStructuredMemoryProvider
{
    Task<StructuredMemoryStatusResponse> GetStatusAsync(CancellationToken ct);
    Task<StructuredMemorySearchResult> SearchAsync(string query, int limit, string? scope, CancellationToken ct);
    Task<StructuredMemoryOpenResult> OpenAsync(string path, int depth, string view, CancellationToken ct);
    Task<StructuredMemoryRecentResult> RecentAsync(int days, int limit, string? scope, CancellationToken ct);
    Task<StructuredMemoryExportResult> ExportAsync(string path, string mode, CancellationToken ct);
    Task<StructuredMemoryHandoffResult> CreateHandoffAsync(string path, CancellationToken ct);
    Task<StructuredMemoryValidationResult> ValidateAsync(CancellationToken ct);
    Task<StructuredMemoryValidationResult> RefreshIndexAsync(CancellationToken ct);
}
