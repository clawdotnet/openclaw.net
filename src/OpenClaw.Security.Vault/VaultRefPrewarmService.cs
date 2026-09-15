using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public sealed class VaultRefPrewarmService : IHostedService
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

    public async Task StartAsync(CancellationToken cancellationToken)
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
            if (!lease.IsAcquired)
            {
                lock (failures) failures.Add((r, "rate-limit timeout"));
                _logger.LogError("Vault pre-warm: ref {Ref} skipped (rate-limit lease timeout).", r);
                return;
            }
            try
            {
                var v = await _resolver.ResolveAsync(r, cancellationToken).ConfigureAwait(false);
                if (v is null)
                {
                    lock (failures) failures.Add((r, "resolve returned null"));
                    _logger.LogError("Vault pre-warm: ref {Ref} resolved to null.", r);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add((r, ex.GetType().Name));
                _logger.LogError(ex, "Vault pre-warm: ref {Ref} failed: {Kind}", r, ex.GetType().Name);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

    private static IEnumerable<(string Path, string Value)> WalkStringsInner(object? root, string path, HashSet<object> visited)
    {
        if (root is null || !visited.Add(root))
            yield break;

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
            else if (val.GetType().IsClass)
            {
                foreach (var inner in WalkStringsInner(val, p, visited))
                    yield return inner;
            }
        }
    }

    private sealed class RateLimiter : IDisposable
    {
        private readonly int _perSecond;
        private readonly SemaphoreSlim _sem;
        private readonly CancellationTokenSource _cts = new();

        public RateLimiter(int perSecond)
        {
            _perSecond = Math.Max(1, perSecond);
            _sem = new SemaphoreSlim(_perSecond, _perSecond);
            _ = RefillLoopAsync(_cts.Token);
        }

        private async Task RefillLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    while (_sem.CurrentCount < _perSecond)
                        _sem.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // disposed
            }
        }

        public async Task<Lease> AcquireAsync(CancellationToken ct)
        {
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(acquired: true);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _sem.Dispose();
            _cts.Dispose();
        }

        public sealed class Lease : IDisposable
        {
            public bool IsAcquired { get; }
            public Lease(bool acquired) => IsAcquired = acquired;
            public void Dispose() { }
        }
    }
}

public sealed class HostingStartupException : Exception
{
    public HostingStartupException(string message) : base(message) { }
}
