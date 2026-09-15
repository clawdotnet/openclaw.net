namespace OpenClaw.Security.Vault;

public readonly record struct VaultRef(string Path, string Key, int KvVersion, string Mount);

public static class VaultRefParser
{
    private const string Prefix = "vault:";

    public static VaultRef Parse(string secretRef, string defaultMount = "secret")
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new VaultRefParseException("Empty vault reference.");

        if (!secretRef.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new VaultRefParseException("Reference must start with 'vault:'.");

        var body = secretRef[Prefix.Length..];
        var hashIdx = body.IndexOf('#');
        if (hashIdx < 0)
            throw new VaultRefParseException("Vault reference must contain '#' separating path and key.");

        var pathPart = body[..hashIdx];
        var key = body[(hashIdx + 1)..];
        if (string.IsNullOrEmpty(key))
            throw new VaultRefParseException("Vault reference key segment is empty (after '#').");

        if (string.IsNullOrEmpty(pathPart))
            throw new VaultRefParseException("Vault reference path segment is empty (before '#').");

        string mount;
        string path;
        var dataIdx = pathPart.IndexOf("/data/", StringComparison.Ordinal);
        if (dataIdx > 0)
        {
            mount = pathPart[..dataIdx];
            path = pathPart[(dataIdx + "/data/".Length)..];
        }
        else
        {
            // No mount segment — treat whole thing as path, apply default mount.
            mount = defaultMount;
            path = pathPart;
        }

        if (string.IsNullOrEmpty(path))
            throw new VaultRefParseException("Vault reference path is empty after mount parsing.");

        return new VaultRef(Path: path, Key: key, KvVersion: 2, Mount: mount);
    }
}
