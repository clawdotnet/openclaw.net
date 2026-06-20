using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenClaw.Plugins.TokenJuice.Rules;

public static class RuleLoader
{
    private static readonly string UserRulesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "tokenjuice", "rules");

    private static readonly ConcurrentDictionary<string, IReadOnlyList<TokenJuiceRule>> _projectCache = new();

    public static IReadOnlyList<TokenJuiceRule> LoadMergedRules(string? projectRoot = null)
    {
        if (projectRoot is not null)
            return _projectCache.GetOrAdd(projectRoot, _ => LoadMergedInternal(projectRoot));

        return LoadMergedInternal(projectRoot);
    }

    private static IReadOnlyList<TokenJuiceRule> LoadMergedInternal(string? projectRoot)
    {
        var merged = new Dictionary<string, TokenJuiceRule>(StringComparer.Ordinal);

        foreach (var rule in LoadBuiltinRules())
            merged[rule.Id] = rule;

        if (Directory.Exists(UserRulesDir))
            foreach (var rule in LoadFromDirectory(UserRulesDir))
                merged[rule.Id] = rule;

        if (projectRoot is not null)
        {
            var projectDir = Path.Combine(projectRoot, ".tokenjuice", "rules");
            if (Directory.Exists(projectDir))
                foreach (var rule in LoadFromDirectory(projectDir))
                    merged[rule.Id] = rule;
        }

        return merged.Values
            .OrderBy(r => r.Id == "generic/fallback" ? 1 : 0)
            .ThenByDescending(r => r.Priority)
            .ToList();
    }

    private static IReadOnlyList<TokenJuiceRule> LoadBuiltinRules()
    {
        var assembly = typeof(RuleLoader).Assembly;
        var rules = new List<TokenJuiceRule>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            // Match any embedded JSON rule resource from this plugin assembly.
            // MSBuild LogicalName uses OS-native path separators (%RecursiveDir),
            // so we avoid prefix matching and use a simple substring check instead.
            if (!resourceName.Contains("TokenJuice", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;

                var rule = JsonSerializer.Deserialize(stream, TokenJuiceJsonContext.Default.TokenJuiceRule);
                if (rule is not null && !string.IsNullOrEmpty(rule.Id))
                    rules.Add(rule);
            }
            catch
            {
                // Skip malformed rule files — fail-open
            }
        }

        return rules.OrderBy(r => r.Id == "generic/fallback" ? 1 : 0)
                    .ThenByDescending(r => r.Priority)
                    .ToList();
    }

    private static IReadOnlyList<TokenJuiceRule> LoadFromDirectory(string dir)
    {
        var rules = new List<TokenJuiceRule>();
        foreach (var path in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            if (path.Replace(Path.DirectorySeparatorChar, '/').Contains("/fixtures/"))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                var rule = JsonSerializer.Deserialize(json, TokenJuiceJsonContext.Default.TokenJuiceRule);
                if (rule is not null && !string.IsNullOrEmpty(rule.Id))
                    rules.Add(rule);
            }
            catch
            {
                // Skip malformed files — fail-open
            }
        }
        return rules;
    }
}
