using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// 集中式密钥解析门面。支持：
/// <list type="bullet">
///   <item><c>env:VAR_NAME</c> — 从环境变量读取</item>
///   <item><c>raw:literal</c>  — 字面量（生产环境不推荐）</item>
///   <item><c>裸串</c>          — 按 env 变量名解析，未命中回退为字面量</item>
///   <item><c>vault:path#key</c> — 从 Vault / OpenBao 拉取（需 OpenClaw.Security.Vault 项目启用）</item>
/// </list>
/// 所有 tools 与 gateway 共享这一实现。DI 引导后委托给 <see cref="ISecretResolver"/>；
/// 否则回退到内嵌 legacy 逻辑，保持向后兼容。
/// </summary>
public static class SecretResolver
{
    private const string EnvPrefix = "env:";
    private const string RawPrefix = "raw:";

    /// <summary>
    /// 解析一个 secret 引用为实际值。当 <paramref name="secretRef"/> 为 null/空白，或
    /// 所引用的环境变量未设置时返回 null。
    /// </summary>
    public static string? Resolve(string? secretRef)
        => Resolve(secretRef, logger: null);

    /// <summary>
    /// 带日志的同步解析。DI 未引导时走 legacy 路径；已引导时委托给注册的 <see cref="ISecretResolver"/>。
    /// </summary>
    public static string? Resolve(string? secretRef, ILogger? logger)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.Resolve(secretRef);
        return LegacyResolve(secretRef, logger);
    }

    /// <summary>
    /// 异步解析。当注册的 provider 支持异步（如 Vault）时，从 Vault / OpenBao 拉取。
    /// </summary>
    public static ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.ResolveAsync(secretRef, ct);
        return ValueTask.FromResult<string?>(LegacyResolve(secretRef, logger: null));
    }

    /// <summary>
    /// 判断是否为 <c>raw:</c> 前缀引用。供 public-bind 加固检查使用。
    /// </summary>
    public static bool IsRawRef(string? secretRef)
        => secretRef is not null && secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase);

    // ── Legacy fallback (DI 未引导时使用) ──────────────────────────────

    private static string? LegacyResolve(string? secretRef, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        var envValue = Environment.GetEnvironmentVariable(secretRef);
        if (envValue is not null)
            return envValue;

        if (logger is not null && LooksLikeEnvVarName(secretRef))
            logger.LogWarning(
                "A secret ref with {Length} chars looks like an environment variable name but no such variable is set. " +
                "Falling back to the literal value. Prefix with 'env:' for strict resolution.",
                secretRef.Length);

        return secretRef;
    }

    private static bool LooksLikeEnvVarName(string value)
        => value.Length >= 3 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
}
