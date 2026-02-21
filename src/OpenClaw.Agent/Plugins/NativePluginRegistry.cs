using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Agent.Tools;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Manages native (C#) replicas of popular OpenClaw plugins.
/// Constructs enabled native tools from configuration.
/// </summary>
public sealed class NativePluginRegistry : IDisposable
{
    private readonly List<ITool> _tools = [];
    private readonly Dictionary<string, string> _nativeToolIds = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public NativePluginRegistry(NativePluginsConfig config, ILogger logger, ToolingConfig? toolingConfig = null)
    {
        _logger = logger;

        if (config.WebSearch.Enabled)
            RegisterTool(new WebSearchTool(config.WebSearch), "web-search", config.WebSearch.Provider);

        if (config.WebFetch.Enabled)
            RegisterTool(new WebFetchTool(config.WebFetch), "web-fetch");

        if (config.GitTools.Enabled)
            RegisterTool(new GitTool(config.GitTools), "git-tools");

        if (config.CodeExec.Enabled)
            RegisterTool(new CodeExecTool(config.CodeExec), "code-exec", config.CodeExec.Backend);

        if (config.ImageGen.Enabled)
            RegisterTool(new ImageGenTool(config.ImageGen), "image-gen", config.ImageGen.Provider);

        if (config.PdfRead.Enabled)
            RegisterTool(new PdfReadTool(config.PdfRead, toolingConfig), "pdf-read");

        if (config.Calendar.Enabled)
            RegisterTool(new CalendarTool(config.Calendar), "calendar", config.Calendar.Provider);

        if (config.Email.Enabled)
            RegisterTool(new EmailTool(config.Email), "email");

        if (config.Database.Enabled)
            RegisterTool(new DatabaseTool(config.Database, _logger), "database", config.Database.Provider);
    }

    private void RegisterTool(ITool tool, string pluginId, string? detail = null)
    {
        if (_nativeToolIds.ContainsKey(tool.Name))
        {
            _logger.LogWarning("Duplicate native tool name '{ToolName}' from plugin '{PluginId}' — overwriting previous registration", tool.Name, pluginId);
            _tools.RemoveAll(t => t.Name == tool.Name);
        }

        _tools.Add(tool);
        _nativeToolIds[tool.Name] = pluginId;
        _logger.LogInformation("Native plugin enabled: {PluginId}{Detail}",
            pluginId, detail is not null ? $" ({detail})" : "");
    }

    /// <summary>
    /// All enabled native plugin tools.
    /// </summary>
    public IReadOnlyList<ITool> Tools => _tools;

    /// <summary>
    /// Check if a tool name is provided by a native replica.
    /// </summary>
    public bool IsNativeTool(string toolName) => _nativeToolIds.ContainsKey(toolName);

    /// <summary>
    /// Get the plugin id for a native tool name (e.g., "web_search" → "web-search").
    /// </summary>
    public string? GetPluginId(string toolName)
        => _nativeToolIds.TryGetValue(toolName, out var id) ? id : null;

    /// <summary>
    /// Resolve which tools to use given bridge tools, native tools, and the preference config.
    /// Returns a deduplicated list: for each tool name, only one implementation wins.
    /// </summary>
    public static List<ITool> ResolvePreference(
        IReadOnlyList<ITool> builtInTools,
        IReadOnlyList<ITool> nativePluginTools,
        IReadOnlyList<ITool> bridgePluginTools,
        PluginsConfig config,
        ILogger logger)
    {
        var result = new List<ITool>(builtInTools);
        var usedNames = new HashSet<string>(
            builtInTools.Select(t => t.Name),
            StringComparer.Ordinal);

        var preferNative = config.Prefer.Equals("native", StringComparison.OrdinalIgnoreCase);

        // Index bridge tools by name for lookup
        var bridgeByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in bridgePluginTools)
        {
            if (!usedNames.Contains(tool.Name))
                bridgeByName[tool.Name] = tool;
        }

        // Index native tools by name for lookup
        var nativeByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in nativePluginTools)
        {
            if (!usedNames.Contains(tool.Name))
                nativeByName[tool.Name] = tool;
        }

        // All tool names from both sources
        var allPluginNames = new HashSet<string>(
            nativeByName.Keys.Concat(bridgeByName.Keys),
            StringComparer.Ordinal);

        foreach (var name in allPluginNames)
        {
            var hasNative = nativeByName.TryGetValue(name, out var nativeTool);
            var hasBridge = bridgeByName.TryGetValue(name, out var bridgeTool);

            // Check for per-tool override
            if (config.Overrides.TryGetValue(name, out var overrideVal))
            {
                if (overrideVal.Equals("native", StringComparison.OrdinalIgnoreCase) && hasNative)
                {
                    result.Add(nativeTool!);
                    logger.LogInformation("Tool '{ToolName}': using native (override)", name);
                }
                else if (overrideVal.Equals("bridge", StringComparison.OrdinalIgnoreCase) && hasBridge)
                {
                    result.Add(bridgeTool!);
                    logger.LogInformation("Tool '{ToolName}': using bridge (override)", name);
                }
                else if (hasNative)
                {
                    result.Add(nativeTool!);
                    logger.LogInformation("Tool '{ToolName}': using native (override fallback)", name);
                }
                else if (hasBridge)
                {
                    result.Add(bridgeTool!);
                    logger.LogInformation("Tool '{ToolName}': using bridge (override fallback)", name);
                }
                continue;
            }

            // Apply default preference
            if (preferNative)
            {
                if (hasNative)
                {
                    result.Add(nativeTool!);
                    if (hasBridge)
                        logger.LogInformation("Tool '{ToolName}': using native (prefer=native, bridge also available)", name);
                }
                else if (hasBridge)
                {
                    result.Add(bridgeTool!);
                }
            }
            else // prefer bridge
            {
                if (hasBridge)
                {
                    result.Add(bridgeTool!);
                    if (hasNative)
                        logger.LogInformation("Tool '{ToolName}': using bridge (prefer=bridge, native also available)", name);
                }
                else if (hasNative)
                {
                    result.Add(nativeTool!);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var tool in _tools)
        {
            if (tool is IDisposable d)
                d.Dispose();
        }
    }
}
