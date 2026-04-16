using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class OrganizationPolicyService
{
    private const string DirectoryName = "admin";
    private const string FileName = "organization-policy.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<OrganizationPolicyService> _logger;
    private OrganizationPolicySnapshot? _cached;

    public OrganizationPolicyService(string storagePath, ILogger<OrganizationPolicyService> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public OrganizationPolicySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return Clone(LoadUnsafe());
        }
    }

    public OrganizationPolicySnapshot Update(OrganizationPolicySnapshot snapshot)
    {
        lock (_gate)
        {
            var normalized = Normalize(snapshot);
            if (!AtomicJsonFileStore.TryWriteAtomic(_path, normalized, CoreJsonContext.Default.OrganizationPolicySnapshot, out var error))
                throw new InvalidOperationException($"Failed to persist organization policy: {error}");

            _cached = normalized;
            return Clone(normalized);
        }
    }

    private OrganizationPolicySnapshot LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        if (!AtomicJsonFileStore.TryLoad(_path, CoreJsonContext.Default.OrganizationPolicySnapshot, out OrganizationPolicySnapshot? loaded, out var error))
            _logger.LogWarning("Failed to load organization policy from {Path}: {Error}", _path, error);

        _cached = Normalize(loaded ?? new OrganizationPolicySnapshot());
        return _cached;
    }

    private static OrganizationPolicySnapshot Normalize(OrganizationPolicySnapshot snapshot)
    {
        var modes = snapshot.AllowedAuthModes
            .Where(static mode => !string.IsNullOrWhiteSpace(mode))
            .Select(static mode => mode.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (modes.Length == 0)
        {
            modes =
            [
                OrganizationAuthModeNames.BootstrapToken,
                OrganizationAuthModeNames.BrowserSession,
                OrganizationAuthModeNames.AccountToken
            ];
        }

        return new OrganizationPolicySnapshot
        {
            BootstrapTokenEnabled = snapshot.BootstrapTokenEnabled,
            AllowedAuthModes = modes,
            MinimumPluginTrustLevel = string.IsNullOrWhiteSpace(snapshot.MinimumPluginTrustLevel) ? "untrusted" : snapshot.MinimumPluginTrustLevel.Trim(),
            ExportRetentionDays = Math.Clamp(snapshot.ExportRetentionDays, 1, 3650),
            RequireInteractiveAdminForHighRiskMutations = snapshot.RequireInteractiveAdminForHighRiskMutations,
            PublicDeploymentGuardrails = snapshot.PublicDeploymentGuardrails
        };
    }

    private static OrganizationPolicySnapshot Clone(OrganizationPolicySnapshot snapshot)
        => new()
        {
            BootstrapTokenEnabled = snapshot.BootstrapTokenEnabled,
            AllowedAuthModes = [.. snapshot.AllowedAuthModes],
            MinimumPluginTrustLevel = snapshot.MinimumPluginTrustLevel,
            ExportRetentionDays = snapshot.ExportRetentionDays,
            RequireInteractiveAdminForHighRiskMutations = snapshot.RequireInteractiveAdminForHighRiskMutations,
            PublicDeploymentGuardrails = snapshot.PublicDeploymentGuardrails
        };
}
