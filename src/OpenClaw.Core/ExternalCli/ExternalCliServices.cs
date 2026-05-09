using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Core.ExternalCli;

public interface IExternalCliConnectorRegistry
{
    IReadOnlyList<ExternalCliConnectorSummary> ListConnectors();
    ExternalCliCommandListResponse ListCommands(string connectorName);
    ExternalCliCommandSchemaResponse GetCommandSchema(string connectorName, string commandName);
    ExternalCliPreparedCommand BuildPreview(ExternalCliPreviewRequest request, bool dryRun);
    Task<ExternalCliConnectorStatus> GetStatusAsync(string connectorName, CancellationToken ct);
}

public interface IExternalCliRunner
{
    Task<ExternalCliExecutionResult> ExecuteAsync(ExternalCliPreparedCommand command, CancellationToken ct);
}

public interface IExternalCliAuditSink
{
    void Record(ExternalCliAuditEntry entry);
}

public interface IExternalCliEventSink
{
    void Record(ExternalCliRuntimeEvent entry);
}

public sealed class NoopExternalCliAuditSink : IExternalCliAuditSink
{
    public void Record(ExternalCliAuditEntry entry) { }
}

public sealed class NoopExternalCliEventSink : IExternalCliEventSink
{
    public void Record(ExternalCliRuntimeEvent entry) { }
}

public sealed class ExternalCliPreparedCommand
{
    public required string ConnectorName { get; init; }
    public required string CommandName { get; init; }
    public required ExternalCliConnectorOptions Connector { get; init; }
    public required ExternalCliCommandOptions Command { get; init; }
    public required string Executable { get; init; }
    public required string[] Arguments { get; init; }
    public required string[] RedactedArguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public required ExternalCliInvocationPreview Preview { get; init; }
    public int MaxStdoutBytes { get; init; }
    public int MaxStderrBytes { get; init; }
    public string[] RedactionRules { get; init; } = [];
}

public sealed class ExternalCliConnectorRegistry : IExternalCliConnectorRegistry
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_\-]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ExternalCliOptions _options;
    private readonly IRedactionPipeline _redaction;

    public ExternalCliConnectorRegistry(GatewayConfig config, IRedactionPipeline? redaction = null)
    {
        _options = config.ExternalCli;
        _redaction = redaction ?? new NoopRedactionPipeline();
    }

    public IReadOnlyList<ExternalCliConnectorSummary> ListConnectors()
        => _options.Connectors
            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static item => new ExternalCliConnectorSummary
            {
                Name = item.Key,
                DisplayName = string.IsNullOrWhiteSpace(item.Value.DisplayName) ? item.Key : item.Value.DisplayName,
                Enabled = item.Value.Enabled,
                Executable = item.Value.Executable,
                CommandCount = item.Value.Commands.Count
            })
            .ToArray();

    public ExternalCliCommandListResponse ListCommands(string connectorName)
    {
        var (name, connector) = GetConnector(connectorName);
        return new ExternalCliCommandListResponse
        {
            Connector = name,
            Items = connector.Commands
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    var command = item.Value;
                    return new ExternalCliCommandSummary
                    {
                        Name = item.Key,
                        Description = command.Description,
                        RiskLevel = ExternalCliRiskLevel.Normalize(command.RiskLevel),
                        ReadOnly = command.ReadOnly,
                        RequiresApproval = RequiresApproval(connector, command),
                        SupportsDryRun = command.SupportsDryRun,
                        StructuredOutput = ResolveOutputFormat(connector, command),
                        Tags = command.Tags
                    };
                })
                .ToArray()
        };
    }

    public ExternalCliCommandSchemaResponse GetCommandSchema(string connectorName, string commandName)
    {
        var (connectorResolvedName, connector) = GetConnector(connectorName);
        var (commandResolvedName, command) = GetCommand(connector, commandName);
        var required = FindPlaceholders(command.ArgsTemplate)
            .Concat(command.Parameters.Where(static item => item.Value.Required).Select(static item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ExternalCliCommandSchemaResponse
        {
            Connector = connectorResolvedName,
            Command = commandResolvedName,
            Description = command.Description,
            Parameters = command.Parameters,
            RequiredParameters = required,
            RiskLevel = ExternalCliRiskLevel.Normalize(command.RiskLevel),
            ReadOnly = command.ReadOnly,
            RequiresApproval = RequiresApproval(connector, command),
            SupportsDryRun = command.SupportsDryRun,
            StructuredOutput = ResolveOutputFormat(connector, command)
        };
    }

    public ExternalCliPreparedCommand BuildPreview(ExternalCliPreviewRequest request, bool dryRun)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("External CLI connectors are disabled.");
        if (_options.AllowFreeformCommands)
            throw new InvalidOperationException("External CLI free-form commands are not supported by this native connector.");

        var (connectorName, connector) = GetConnector(request.Connector);
        if (!connector.Enabled)
            throw new InvalidOperationException($"External CLI connector '{connectorName}' is disabled.");
        if (string.IsNullOrWhiteSpace(connector.Executable))
            throw new InvalidOperationException($"External CLI connector '{connectorName}' has no executable configured.");

        var (commandName, command) = GetCommand(connector, request.Command);
        if (dryRun && (!command.SupportsDryRun || command.DryRunArgsTemplate.Length == 0))
            throw new InvalidOperationException($"External CLI command '{connectorName}/{commandName}' does not have an explicit dry-run template.");

        var template = dryRun ? command.DryRunArgsTemplate : command.ArgsTemplate;
        if (template.Length == 0)
            throw new InvalidOperationException($"External CLI command '{connectorName}/{commandName}' has no argument template.");

        ValidateParameters(command, template, request.Parameters);
        var args = connector.DefaultArgs.Concat(ExpandTemplate(template, request.Parameters)).ToArray();
        var redactionRules = connector.RedactionRules.Concat(command.RedactionRules).ToArray();
        var redactedArgs = RedactArgs(args, redactionRules);
        var workingDirectory = ResolveWorkingDirectory(command.WorkingDirectory ?? connector.WorkingDirectory);
        var timeout = Math.Clamp(command.TimeoutSeconds ?? _options.DefaultTimeoutSeconds, 1, 3600);
        var outputFormat = ResolveOutputFormat(connector, command);
        var environment = new Dictionary<string, string>(connector.Environment, StringComparer.Ordinal);
        foreach (var (key, value) in command.Environment)
            environment[key] = value;
        var resolvedExecutable = ResolveExecutable(connector.Executable, environment) ?? connector.Executable;

        var fingerprint = ComputeFingerprint(
            connectorName,
            commandName,
            resolvedExecutable,
            args,
            workingDirectory,
            timeout,
            outputFormat,
            dryRun,
            command);
        var parametersHash = ComputeParametersHash(request.Parameters);
        var requiresApproval = RequiresApproval(connector, command);
        var preview = new ExternalCliInvocationPreview
        {
            Connector = connectorName,
            Command = commandName,
            Executable = resolvedExecutable,
            Arguments = args,
            RedactedArguments = redactedArgs,
            RedactedCommandLine = BuildCommandLine(resolvedExecutable, redactedArgs),
            RiskLevel = ExternalCliRiskLevel.Normalize(command.RiskLevel),
            ReadOnly = command.ReadOnly,
            RequiresApproval = requiresApproval,
            SupportsDryRun = command.SupportsDryRun,
            IsDryRun = dryRun,
            StructuredOutput = outputFormat,
            RequiredScopes = command.RequiredScopes,
            RequiredIdentity = command.RequiredIdentity,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeout,
            Fingerprint = fingerprint,
            ParametersHash = parametersHash,
            Warnings = BuildPreviewWarnings(connector.Executable, environment)
        };

        return new ExternalCliPreparedCommand
        {
            ConnectorName = connectorName,
            CommandName = commandName,
            Connector = connector,
            Command = command,
            Executable = resolvedExecutable,
            Arguments = args,
            RedactedArguments = redactedArgs,
            WorkingDirectory = workingDirectory,
            Environment = environment,
            Preview = preview,
            MaxStdoutBytes = Math.Clamp(_options.MaxStdoutBytes, 1, 16 * 1024 * 1024),
            MaxStderrBytes = Math.Clamp(_options.MaxStderrBytes, 1, 4 * 1024 * 1024),
            RedactionRules = redactionRules
        };
    }

    public async Task<ExternalCliConnectorStatus> GetStatusAsync(string connectorName, CancellationToken ct)
    {
        var (name, connector) = GetConnector(connectorName);
        var warnings = new List<string>();
        var resolvedExecutable = ResolveExecutable(connector.Executable, connector.Environment);
        if (string.IsNullOrWhiteSpace(resolvedExecutable))
            warnings.Add($"Executable '{connector.Executable}' was not found on PATH.");

        string? version = null;
        if (!_options.Enabled)
            warnings.Add("External CLI connectors are disabled.");
        if (!connector.Enabled)
            warnings.Add($"External CLI connector '{name}' is disabled.");

        var executableForStatus = _options.Enabled && connector.Enabled ? resolvedExecutable : null;
        if (executableForStatus is not null && connector.VersionCommand is { Args.Length: > 0 } versionCommand)
            version = await RunStatusCommandAsync(executableForStatus, versionCommand, connector, ct);

        string authStatus = "unknown";
        bool? authenticated = null;
        if (executableForStatus is not null && connector.StatusCommand is { Args.Length: > 0 } statusCommand)
        {
            var statusOutput = await RunStatusCommandAsync(executableForStatus, statusCommand, connector, ct, captureExitCode: true) ?? "";
            if (statusOutput.StartsWith("exit=0\n", StringComparison.Ordinal))
            {
                authenticated = true;
                authStatus = "authenticated";
            }
            else if (statusOutput.StartsWith("exit=", StringComparison.Ordinal))
            {
                authenticated = false;
                authStatus = "not_authenticated";
            }
        }

        return new ExternalCliConnectorStatus
        {
            Connector = name,
            DisplayName = string.IsNullOrWhiteSpace(connector.DisplayName) ? name : connector.DisplayName,
            Enabled = connector.Enabled,
            Executable = connector.Executable,
            ExecutableFound = resolvedExecutable is not null,
            ResolvedExecutablePath = resolvedExecutable,
            Version = version,
            Authenticated = authenticated,
            AuthenticationStatus = authStatus,
            Warnings = warnings,
            LastCheckedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private (string Name, ExternalCliConnectorOptions Connector) GetConnector(string? connectorName)
    {
        if (string.IsNullOrWhiteSpace(connectorName))
            throw new InvalidOperationException("External CLI connector is required.");

        if (!_options.Connectors.TryGetValue(connectorName.Trim(), out var connector))
            throw new InvalidOperationException($"Unknown external CLI connector '{connectorName}'.");

        var resolvedName = _options.Connectors.Keys.First(item => string.Equals(item, connectorName.Trim(), StringComparison.OrdinalIgnoreCase));
        return (resolvedName, connector);
    }

    private static (string Name, ExternalCliCommandOptions Command) GetCommand(ExternalCliConnectorOptions connector, string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            throw new InvalidOperationException("External CLI command is required.");

        if (!connector.Commands.TryGetValue(commandName.Trim(), out var command))
            throw new InvalidOperationException($"Unknown external CLI command '{commandName}'.");

        var resolvedName = connector.Commands.Keys.First(item => string.Equals(item, commandName.Trim(), StringComparison.OrdinalIgnoreCase));
        return (resolvedName, command);
    }

    private bool RequiresApproval(ExternalCliConnectorOptions connector, ExternalCliCommandOptions command)
        => connector.RequiresApproval
           || command.RequiresApproval
           || ExternalCliRiskLevel.Normalize(command.RiskLevel) == ExternalCliRiskLevel.High
           || (!command.ReadOnly && _options.RequireApprovalForMutatingCommands);

    private static string ResolveOutputFormat(ExternalCliConnectorOptions connector, ExternalCliCommandOptions command)
        => ExternalCliOutputFormat.Normalize(string.IsNullOrWhiteSpace(command.StructuredOutput)
            ? connector.DefaultOutputFormat
            : command.StructuredOutput);

    private static void ValidateParameters(
        ExternalCliCommandOptions command,
        IReadOnlyList<string> template,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var placeholderNames = FindPlaceholders(template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredNames = command.Parameters.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedNames = placeholderNames.Concat(declaredNames).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in placeholderNames)
        {
            if (!TryGetParameter(parameters, required, out _))
                throw new InvalidOperationException($"External CLI parameter '{required}' is required.");
        }

        foreach (var (name, schema) in command.Parameters)
        {
            if (schema.Required && !TryGetParameter(parameters, name, out _))
                throw new InvalidOperationException($"External CLI parameter '{name}' is required.");
        }

        if (!command.AllowUnknownParameters)
        {
            foreach (var key in parameters.Keys)
            {
                if (!allowedNames.Contains(key))
                    throw new InvalidOperationException($"External CLI parameter '{key}' is not allowed for this command.");
            }
        }

        foreach (var (key, value) in parameters)
        {
            if (TryGetParameterSchema(command.Parameters, key, out var schema))
                ValidateParameterValue(key, ToParameterString(key, value), schema);
            else
                _ = ToParameterString(key, value);
        }
    }

    private static void ValidateParameterValue(string name, string value, ExternalCliParameterOptions schema)
    {
        if (schema.MaxLength is { } maxLength && value.Length > maxLength)
            throw new InvalidOperationException($"External CLI parameter '{name}' exceeds max length {maxLength}.");

        if (schema.AllowedValues.Length > 0 &&
            !schema.AllowedValues.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"External CLI parameter '{name}' has a value that is not allowed.");
        }

        if (!string.IsNullOrWhiteSpace(schema.Pattern) &&
            !Regex.IsMatch(value, schema.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
        {
            throw new InvalidOperationException($"External CLI parameter '{name}' does not match the required pattern.");
        }
    }

    private static string[] ExpandTemplate(IReadOnlyList<string> template, IReadOnlyDictionary<string, JsonElement> parameters)
        => template
            .Select(arg => PlaceholderRegex.Replace(arg, match =>
            {
                var name = match.Groups["name"].Value;
                return TryGetParameter(parameters, name, out var value)
                    ? ToParameterString(name, value)
                    : throw new InvalidOperationException($"External CLI parameter '{name}' is required.");
            }))
            .ToArray();

    private static bool TryGetParameter(IReadOnlyDictionary<string, JsonElement> parameters, string name, out JsonElement value)
    {
        if (parameters.TryGetValue(name, out value))
            return true;

        foreach (var (key, candidate) in parameters)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetParameterSchema(IReadOnlyDictionary<string, ExternalCliParameterOptions> parameters, string name, out ExternalCliParameterOptions schema)
    {
        if (parameters.TryGetValue(name, out schema!))
            return true;

        foreach (var (key, candidate) in parameters)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                schema = candidate;
                return true;
            }
        }

        schema = null!;
        return false;
    }

    private static IReadOnlyList<string> FindPlaceholders(IReadOnlyList<string> template)
        => template
            .SelectMany(static item => PlaceholderRegex.Matches(item).Select(match => match.Groups["name"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ToParameterString(string name, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException($"External CLI parameter '{name}' must be a string, number, or boolean.")
        };

    private string[] RedactArgs(IReadOnlyList<string> args, IReadOnlyList<string> redactionRules)
        => args.Select(arg => Redact(arg, redactionRules)).ToArray();

    private string Redact(string value, IReadOnlyList<string> redactionRules)
    {
        if (!_options.RedactSecrets)
            return value;

        var current = _redaction.Redact(value);
        foreach (var pattern in redactionRules)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            try
            {
                current = Regex.Replace(
                    current,
                    pattern,
                    "[REDACTED:external-cli]",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                // Invalid config is reported by startup validation; keep redaction best-effort.
                continue;
            }
        }

        return current;
    }

    private static string? ResolveWorkingDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || string.Equals(expanded, "~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
        }

        var fullPath = Path.GetFullPath(expanded);
        if (!Directory.Exists(fullPath))
            throw new InvalidOperationException($"External CLI working directory '{configured}' does not exist.");

        return fullPath;
    }

    public static string? ResolveExecutable(string executable, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        if (Path.IsPathFullyQualified(executable))
            return File.Exists(executable) ? executable : null;

        var path = environment is not null && environment.TryGetValue("PATH", out var configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var candidates = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ext => executable.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? executable : executable + ext)
                .Append(executable)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [executable];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    private async Task<string?> RunStatusCommandAsync(
        string executable,
        ExternalCliStatusCommandOptions command,
        ExternalCliConnectorOptions connector,
        CancellationToken ct,
        bool captureExitCode = false)
    {
        var timeout = Math.Clamp(command.TimeoutSeconds ?? Math.Min(_options.DefaultTimeoutSeconds, 20), 1, 120);
        var workingDirectory = ResolveWorkingDirectory(connector.WorkingDirectory);
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var arg in connector.DefaultArgs.Concat(command.Args))
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in connector.Environment)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var output = (await outputTask).Trim();
            if (string.IsNullOrWhiteSpace(output))
                output = (await errorTask).Trim();
            output = Redact(output, connector.RedactionRules);
            return captureExitCode ? $"exit={process.ExitCode}\n{output}" : output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Best-effort cleanup; process may already have exited. */ }
            catch (NotSupportedException) { /* Best-effort cleanup; process-tree kill may be unsupported. */ }
            catch (Win32Exception) { /* Best-effort cleanup; OS process state may already be gone. */ }
            return captureExitCode ? "exit=-1\ntimeout" : "timeout";
        }
    }

    private static IReadOnlyList<string> BuildPreviewWarnings(string executable, IReadOnlyDictionary<string, string>? environment)
    {
        var warnings = new List<string>();
        if (ResolveExecutable(executable, environment) is null)
            warnings.Add($"Executable '{executable}' was not found on PATH.");
        return warnings;
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> args)
        => args.Count == 0
            ? executable
            : $"{executable} {string.Join(' ', args.Select(QuoteForPreview))}";

    private static string QuoteForPreview(string arg)
        => arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : arg;

    private static string ComputeFingerprint(
        string connectorName,
        string commandName,
        string executable,
        IReadOnlyList<string> args,
        string? workingDirectory,
        int timeoutSeconds,
        string outputFormat,
        bool dryRun,
        ExternalCliCommandOptions command)
    {
        var builder = new StringBuilder();
        builder.Append(connectorName).Append('\n')
            .Append(commandName).Append('\n')
            .Append(executable).Append('\n')
            .Append(workingDirectory ?? "").Append('\n')
            .Append(timeoutSeconds).Append('\n')
            .Append(outputFormat).Append('\n')
            .Append(dryRun ? "dry-run" : "execute").Append('\n')
            .Append(ExternalCliRiskLevel.Normalize(command.RiskLevel)).Append('\n')
            .Append(command.ReadOnly ? "readonly" : "mutation").Append('\n');
        foreach (var arg in args)
            builder.Append(arg).Append('\n');

        return Sha256(builder.ToString());
    }

    private static string ComputeParametersHash(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var builder = new StringBuilder();
        foreach (var key in parameters.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            builder.Append(key).Append('=').Append(parameters[key].GetRawText()).Append('\n');
        return Sha256(builder.ToString());
    }

    public static string ComputeArgsHash(IReadOnlyList<string> args)
        => Sha256(string.Join('\n', args));

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class ExternalCliRunner : IExternalCliRunner
{
    private readonly IRedactionPipeline _redaction;

    public ExternalCliRunner(IRedactionPipeline? redaction = null)
    {
        _redaction = redaction ?? new NoopRedactionPipeline();
    }

    public async Task<ExternalCliExecutionResult> ExecuteAsync(ExternalCliPreparedCommand command, CancellationToken ct)
    {
        var executable = command.Executable;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            psi.WorkingDirectory = command.WorkingDirectory;
        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in command.Environment)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            return new ExternalCliExecutionResult
            {
                Preview = command.Preview,
                Success = false,
                ExitCode = -1,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = _redaction.Redact(ex.Message)
            };
        }
        catch (Win32Exception ex)
        {
            sw.Stop();
            return new ExternalCliExecutionResult
            {
                Preview = command.Preview,
                Success = false,
                ExitCode = -1,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = _redaction.Redact(ex.Message)
            };
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, command.MaxStdoutBytes, ct);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, command.MaxStderrBytes, ct);
        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(command.Preview.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Best-effort cleanup; process may already have exited. */ }
            catch (NotSupportedException) { /* Best-effort cleanup; process-tree kill may be unsupported. */ }
            catch (Win32Exception) { /* Best-effort cleanup; OS process state may already be gone. */ }
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { /* Best-effort cleanup; process may already have detached. */ }
            catch (OperationCanceledException) { /* Defensive; cleanup wait is non-critical after timeout. */ }
            catch (Win32Exception) { /* Best-effort cleanup; OS process state may already be gone. */ }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();
        var stdoutText = Redact(Encoding.UTF8.GetString(stdout.Bytes), command.RedactionRules);
        var stderrText = Redact(Encoding.UTF8.GetString(stderr.Bytes), command.RedactionRules);
        var outputFormat = ExternalCliOutputFormat.Normalize(command.Preview.StructuredOutput);
        var (parsedJson, parseError) = ParseStructuredOutput(outputFormat, stdoutText);
        var exitCode = timedOut ? -1 : SafeExitCode(process);

        return new ExternalCliExecutionResult
        {
            Preview = command.Preview,
            Success = !timedOut && exitCode == 0,
            ExitCode = exitCode,
            Stdout = stdoutText,
            Stderr = stderrText,
            StdoutTruncated = stdout.Truncated,
            StderrTruncated = stderr.Truncated,
            TimedOut = timedOut,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ParsedJson = parsedJson,
            ParseError = parseError,
            ErrorMessage = timedOut ? "External CLI command timed out." : null
        };
    }

    private string Redact(string value, IReadOnlyList<string> redactionRules)
    {
        var current = _redaction.Redact(value);
        foreach (var pattern in redactionRules)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            try
            {
                current = Regex.Replace(
                    current,
                    pattern,
                    "[REDACTED:external-cli]",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                // Invalid config is reported by startup validation; keep redaction best-effort.
                continue;
            }
        }

        return current;
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 64 * 1024));
        var remaining = maxBytes;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            if (read > remaining)
            {
                ms.Write(buffer, 0, remaining);
                remaining = 0;
                truncated = true;
                continue;
            }

            ms.Write(buffer, 0, read);
            remaining -= read;
        }

        return (ms.ToArray(), truncated);
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
        catch (Win32Exception)
        {
            return -1;
        }
    }

    private static (JsonElement? Parsed, string? Error) ParseStructuredOutput(string outputFormat, string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return (null, null);

        try
        {
            if (outputFormat == ExternalCliOutputFormat.Json)
            {
                using var document = JsonDocument.Parse(stdout);
                return (document.RootElement.Clone(), null);
            }

            if (outputFormat == ExternalCliOutputFormat.Ndjson)
            {
                var lines = stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();
                if (lines.Length == 0)
                    return (null, null);

                using var document = JsonDocument.Parse("[" + string.Join(',', lines) + "]");
                return (document.RootElement.Clone(), null);
            }
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }

        return (null, null);
    }
}
