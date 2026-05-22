using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class FileGovernanceLedgerStore : IGovernanceLedgerStore
{
    private readonly string _ledgerPath;
    private readonly string _ledgerPathPrefix;

    public FileGovernanceLedgerStore(string storagePath)
    {
        var root = Path.GetFullPath(storagePath);
        _ledgerPath = Path.GetFullPath(Path.Join(root, "harness", "governance"));
        _ledgerPathPrefix = _ledgerPath.EndsWith(Path.DirectorySeparatorChar)
            ? _ledgerPath
            : _ledgerPath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_ledgerPath);
    }

    public ValueTask SaveAsync(GovernanceLedgerEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureSafeId(entry.Id);
        return SaveOneAsync(FileForId(entry.Id), entry, ct);
    }

    public ValueTask<GovernanceLedgerEntry?> GetAsync(string id, CancellationToken ct)
    {
        EnsureSafeId(id);
        return LoadOneAsync(FileForId(id), ct);
    }

    public async ValueTask<IReadOnlyList<GovernanceLedgerEntry>> ListAsync(GovernanceLedgerListQuery query, CancellationToken ct)
    {
        query ??= new GovernanceLedgerListQuery();
        var results = new List<GovernanceLedgerEntry>();
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_ledgerPath).EnumerateFiles("*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = await LoadOneAsync(file, ct);
                if (entry is not null && Matches(entry, query))
                    results.Add(entry);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("Skipping invalid governance ledger file '{0}': {1}", file.FullName, ex.Message);
            }
        }

        var ordered = results
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ThenByDescending(static item => item.CreatedAtUtc);
        return query.Limit <= 0
            ? ordered.ToArray()
            : ordered.Take(Math.Clamp(query.Limit, 1, 5000)).ToArray();
    }

    public async ValueTask<GovernanceLedgerEntry?> RevokeAsync(string id, string revokedBy, string reason, CancellationToken ct)
    {
        EnsureSafeId(id);
        if (string.IsNullOrWhiteSpace(revokedBy))
            throw new ArgumentException("Governance ledger revocation actor is required.", nameof(revokedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Governance ledger revocation reason is required.", nameof(reason));

        var existing = await LoadOneAsync(FileForId(id), ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var revoked = new GovernanceLedgerEntry
        {
            Id = existing.Id,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = now,
            Decision = existing.Decision,
            Status = GovernanceDecisionStatuses.Revoked,
            Source = existing.Source,
            ActionType = existing.ActionType,
            ToolName = existing.ToolName,
            ActionSummary = existing.ActionSummary,
            ArgumentSummary = existing.ArgumentSummary,
            RedactedArguments = existing.RedactedArguments,
            RiskLevel = existing.RiskLevel,
            Scope = existing.Scope,
            ScopeKey = existing.ScopeKey,
            SessionId = existing.SessionId,
            HarnessContractId = existing.HarnessContractId,
            EvidenceBundleId = existing.EvidenceBundleId,
            LearningProposalId = existing.LearningProposalId,
            ApprovalId = existing.ApprovalId,
            ActorId = existing.ActorId,
            ChannelId = existing.ChannelId,
            SenderId = existing.SenderId,
            DecidedBy = existing.DecidedBy,
            DecisionReason = existing.DecisionReason,
            ExpiresAtUtc = existing.ExpiresAtUtc,
            RevokedAtUtc = now,
            RevokedBy = revokedBy.Trim(),
            RevocationReason = reason.Trim(),
            PolicyHint = existing.PolicyHint,
            Tags = existing.Tags,
            Metadata = existing.Metadata
        };
        await SaveOneAsync(FileForId(id), revoked, ct);
        return revoked;
    }

    private FileInfo FileForId(string id)
    {
        var expectedFileName = $"{EncodeKey(id)}.json";
        var fileName = Path.GetFileName(expectedFileName);
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, expectedFileName, StringComparison.Ordinal))
            throw new ArgumentException("Governance ledger id resolves to an unsafe file name.", nameof(id));

        var path = Path.GetFullPath(Path.Join(_ledgerPath, fileName));
        if (!path.StartsWith(_ledgerPathPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Governance ledger id resolves outside the ledger store.", nameof(id));

        return new FileInfo(path);
    }

    private static bool Matches(GovernanceLedgerEntry entry, GovernanceLedgerListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Decision) &&
            !string.Equals(entry.Decision, query.Decision, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !string.Equals(entry.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ToolName) &&
            !string.Equals(entry.ToolName, query.ToolName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ActionType) &&
            !string.Equals(entry.ActionType, query.ActionType, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.RiskLevel) &&
            !string.Equals(entry.RiskLevel, query.RiskLevel, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Scope) &&
            !string.Equals(entry.Scope, query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SessionId) &&
            !string.Equals(entry.SessionId, query.SessionId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ActorId) &&
            !string.Equals(entry.ActorId, query.ActorId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(entry.ChannelId, query.ChannelId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.DecidedBy) &&
            !string.Equals(entry.DecidedBy, query.DecidedBy, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            (entry.Tags?.Any(tag => string.Equals(tag, query.Tag, StringComparison.OrdinalIgnoreCase)) != true))
            return false;

        if (query.CreatedFromUtc is { } fromUtc && entry.CreatedAtUtc < fromUtc)
            return false;

        if (query.CreatedToUtc is { } toUtc && entry.CreatedAtUtc > toUtc)
            return false;

        return true;
    }

    private static async ValueTask<GovernanceLedgerEntry?> LoadOneAsync(FileInfo file, CancellationToken ct)
    {
        if (!file.Exists)
            return default;

        try
        {
            await using var stream = file.OpenRead();
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.GovernanceLedgerEntry, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static async ValueTask SaveOneAsync(FileInfo file, GovernanceLedgerEntry entry, CancellationToken ct)
    {
        file.Directory?.Create();
        var tempFile = new FileInfo($"{file.FullName}.{Guid.NewGuid():N}.tmp");
        var tempPath = tempFile.FullName;
        try
        {
            await using (var stream = tempFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entry, CoreJsonContext.Default.GovernanceLedgerEntry, ct);
            }

            tempFile.MoveTo(file.FullName, overwrite: true);
        }
        finally
        {
            var cleanupFile = new FileInfo(tempPath);
            try
            {
                if (cleanupFile.Exists)
                    cleanupFile.Delete();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Trace.TraceWarning("Failed to delete temp governance ledger file '{0}': {1}", cleanupFile.FullName, ex);
            }
        }
    }

    private static void EnsureSafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Governance ledger id is required.", nameof(id));

        if (id.Length > 128)
            throw new ArgumentException("Governance ledger id is too long.", nameof(id));

        if (!id.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            throw new ArgumentException("Governance ledger id contains unsafe characters.", nameof(id));
    }

    private static string EncodeKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
