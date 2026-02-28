using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.SemanticKernelAdapter;

public static class SemanticKernelAdapterExtensions
{
    /// <summary>
    /// Registers Semantic Kernel interop services.
    /// This does not automatically add tools to OpenClaw; you still need to append them to the tool list.
    /// </summary>
    public static IServiceCollection AddSemanticKernelInterop(
        this IServiceCollection services,
        Action<SemanticKernelInteropOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        return services;
    }

    /// <summary>
    /// Create OpenClaw tools from a discovery kernel.
    /// </summary>
    public static IReadOnlyList<ITool> CreateToolsFromKernel(
        this Kernel discoveryKernel,
        Func<CancellationToken, ValueTask<Kernel>> kernelFactory,
        SemanticKernelInteropOptions? options = null)
        => SemanticKernelToolFactory.CreateTools(kernelFactory, discoveryKernel, options);

    /// <summary>
    /// Bind mapping options from configuration and create tools.
    /// Adapter-only convenience method.
    /// </summary>
    public static IReadOnlyList<ITool> AddSemanticKernelToolsFromConfig(
        IConfiguration config,
        Kernel discoveryKernel,
        Func<CancellationToken, ValueTask<Kernel>> kernelFactory,
        string sectionName = "OpenClaw:SemanticKernel")
    {
        var section = config.GetSection(sectionName);
        var mapping = section.GetSection("ToolMapping").Get<SemanticKernelToolMappingOptions>() ?? new SemanticKernelToolMappingOptions();
        var interop = section.GetSection("Interop").Get<SemanticKernelInteropOptions>() ?? new SemanticKernelInteropOptions();

        if (!mapping.Enabled)
            return Array.Empty<ITool>();

        interop.ToolNamePrefix = mapping.NamePrefix;
        interop.MaxMappedTools = mapping.MaxTools;
        if (mapping.Plugins.Length > 0)
            interop.AllowedPlugins = mapping.Plugins;

        return SemanticKernelToolFactory.CreateTools(kernelFactory, discoveryKernel, interop);
    }

    /// <summary>
    /// Register policy options and the policy hook.
    /// The hook must be added to OpenClaw's tool hook list by the host.
    /// </summary>
    public static IServiceCollection AddSemanticKernelPolicyHook(
        this IServiceCollection services,
        Action<SemanticKernelPolicyOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<SemanticKernelPolicyHook>();
        return services;
    }
}
