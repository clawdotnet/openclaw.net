using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public sealed class VaultRefPrewarmService : BackgroundService
{
    private readonly VaultSecurityOptions _options;
    private readonly ISecretResolver _resolver;
    private readonly IServiceProvider _services;
    private readonly ILogger<VaultRefPrewarmService> _logger;

    public VaultRefPrewarmService(
        VaultSecurityOptions options,
        ISecretResolver resolver,
        IServiceProvider services,
        ILogger<VaultRefPrewarmService> logger)
    {
        _options = options;
        _resolver = resolver;
        _services = services;
        _logger = logger;
    }

    private Task? _warmTask;
    private readonly object _warmLock = new();
    public Task WarmAsync(CancellationToken cancellationToken)
    {
        lock (_warmLock) return _warmTask ??= WarmCoreAsync(cancellationToken);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await WarmAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(_options.CacheTtl / 2);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await WarmCoreAsync(stoppingToken).ConfigureAwait(false); }
            catch (HostingStartupException)
            { _logger.LogWarning("Vault periodic refresh failed; expired cache entries remain unavailable."); }
        }
    }

    private async Task WarmCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var refs = new HashSet<string>(_options.PrewarmRefs, StringComparer.Ordinal);
        // Scan gateway config for vault:* refs
        var scanned = ScanConfigForVaultRefs();
        foreach (var s in scanned) refs.Add(s);

        if (refs.Count == 0)
        {
            _logger.LogInformation("Vault pre-warm: no refs to resolve.");
            return;
        }

        var failures = new List<(string Ref, string Reason)>();

        using var limiter = new RateLimiter(_options.RateLimit.RequestsPerSecond);
        var tasks = refs.Select(async r =>
        {
            using var lease = await limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var v = await _resolver.ResolveAsync(r, cancellationToken).ConfigureAwait(false);
                if (v is null)
                {
                    lock (failures) failures.Add((r, "resolve returned null"));
                    _logger.LogError("Vault pre-warm: ref {Ref} resolved to null.", r);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation aborts pre-warm entirely; it is not a per-ref failure.
                throw;
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add((r, ex.GetType().Name));
                _logger.LogError("Vault pre-warm: ref {Ref} failed: {Kind}", r, ex.GetType().Name);
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (failures.Count == 0)
        {
            _logger.LogInformation("Vault pre-warm: {Count} refs OK.", refs.Count);
            return;
        }

        if (_options.PrewarmRequired)
        {
            throw new HostingStartupException(
                $"Vault pre-warm failed for {failures.Count} of {refs.Count} refs: " +
                string.Join(", ", failures.Select(f => $"{f.Ref} ({f.Reason})")));
        }

        _logger.LogWarning("Vault pre-warm completed with {Failures} of {Total} failures; continuing in soft-fail mode.",
            failures.Count, refs.Count);
    }


    private IEnumerable<string> ScanConfigForVaultRefs()
    {
        // Resolve GatewayConfig from DI; reflection-walk all *Ref string properties with "vault:" prefix.
        var config = _services.GetService<OpenClaw.Core.Models.GatewayConfig>();
        if (config is null) yield break;

        foreach (var (_, value) in WalkStrings(config, "GatewayConfig"))
        {
            if (value.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
                yield return value;
        }
    }

    private static IEnumerable<(string Path, string Value)> WalkStrings(object root, string path)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in WalkStringsInner(root, path, visited))
            yield return item;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Values reached by this walk are instances of the strongly-typed GatewayConfig model. " +
                        "CoreJsonContext ([JsonSerializable(typeof(GatewayConfig))]) roots the entire config graph " +
                        "via System.Text.Json source generation, so their public properties are preserved in AOT builds.")]
    private static IEnumerable<(string Path, string Value)> WalkStringsInner(object? root, string path, HashSet<object> visited)
    {
        if (root is null || !visited.Add(root))
            yield break;

        if (root is string text) { yield return (path, text); yield break; }
        if (root is System.Text.Json.JsonElement json)
        {
            foreach (var item in WalkJson(json, path)) yield return item;
            yield break;
        }
        var t = root.GetType();
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? val;
            try { val = prop.GetValue(root); } catch { continue; }
            if (val is null) continue;
            var p = $"{path}.{prop.Name}";

            if (val is string s)
            {
                yield return (p, s);
            }
            else if (val is IEnumerable e)
            {
                int i = 0;
                foreach (var item in e)
                {
                    foreach (var inner in WalkStringsInner(item, $"{p}[{i}]", visited))
                        yield return inner;
                    i++;
                }
            }
            else if (val is System.Text.Json.JsonElement || val.GetType().IsClass)
            {
                foreach (var inner in WalkStringsInner(val, p, visited))
                    yield return inner;
            }
        }
    }

    private static IEnumerable<(string Path, string Value)> WalkJson(System.Text.Json.JsonElement json, string path)
    {
        if (json.ValueKind == System.Text.Json.JsonValueKind.String) yield return (path, json.GetString()!);
        else if (json.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var property in json.EnumerateObject())
                foreach (var item in WalkJson(property.Value, path + "." + property.Name)) yield return item;
        else if (json.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var child in json.EnumerateArray())
                foreach (var item in WalkJson(child, path)) yield return item;
    }

    private sealed class RateLimiter : IDisposable
    {
        private readonly SemaphoreSlim _sem;

        public RateLimiter(int maxConcurrency)
        {
            _sem = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        }

        public async Task<Lease> AcquireAsync(CancellationToken ct)
        {
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(_sem);
        }

        public void Dispose() => _sem.Dispose();

        public sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _sem;

            internal Lease(SemaphoreSlim sem) => _sem = sem;

            // Releases the permit exactly once, even if Dispose is called again.
            public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
        }
    }
}

public sealed class HostingStartupException : Exception
{
    public HostingStartupException(string message) : base(message) { }
}
