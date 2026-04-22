using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

public sealed class LocalSetupStateSnapshot
{
    public int OperatorAccountCount { get; init; }
    public OrganizationPolicySnapshot? Policy { get; init; }
    public SetupVerificationSnapshot? VerificationSnapshot { get; init; }
}

public static class LocalSetupStateLoader
{
    public static LocalSetupStateSnapshot Load(string storagePath)
    {
        var rootedStorage = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        var adminDirectory = Path.Combine(rootedStorage, "admin");
        var operatorAccountsPath = Path.Combine(adminDirectory, "operator-accounts.json");
        var organizationPolicyPath = Path.Combine(adminDirectory, "organization-policy.json");

        return new LocalSetupStateSnapshot
        {
            OperatorAccountCount = ReadOperatorAccountCount(operatorAccountsPath),
            Policy = ReadOrganizationPolicy(organizationPolicyPath),
            VerificationSnapshot = new SetupVerificationSnapshotStore(rootedStorage).Load()
        };
    }

    private static int ReadOperatorAccountCount(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("accounts", out var accounts) &&
                accounts.ValueKind == JsonValueKind.Array)
            {
                return accounts.GetArrayLength();
            }
        }
        catch
        {
        }

        return 0;
    }

    private static OrganizationPolicySnapshot? ReadOrganizationPolicy(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.OrganizationPolicySnapshot);
        }
        catch
        {
            return null;
        }
    }
}
