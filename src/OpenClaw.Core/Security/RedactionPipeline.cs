using System.Text.RegularExpressions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Security;

public interface ISensitiveDataRedactor
{
    string Name { get; }
    string Redact(string? value);
}

public interface IRedactionPipeline
{
    string Redact(string? value);
    void RedactSessionInPlace(Session session);
}

public sealed class RedactionPipeline : IRedactionPipeline
{
    private readonly IReadOnlyList<ISensitiveDataRedactor> _redactors;

    public RedactionPipeline(IEnumerable<ISensitiveDataRedactor> redactors)
    {
        _redactors = redactors.ToArray();
    }

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var current = value;
        foreach (var redactor in _redactors)
            current = redactor.Redact(current);
        return current;
    }

    public void RedactSessionInPlace(Session session)
    {
        for (var i = 0; i < session.History.Count; i++)
        {
            var turn = session.History[i];
            List<ToolInvocation>? toolCalls = null;
            if (turn.ToolCalls is not null)
            {
                toolCalls = new List<ToolInvocation>(turn.ToolCalls.Count);
                foreach (var toolCall in turn.ToolCalls)
                {
                    toolCalls.Add(toolCall with
                    {
                        Arguments = Redact(toolCall.Arguments),
                        Result = toolCall.Result is null ? null : Redact(toolCall.Result),
                        FailureMessage = toolCall.FailureMessage is null ? null : Redact(toolCall.FailureMessage),
                        NextStep = toolCall.NextStep is null ? null : Redact(toolCall.NextStep)
                    });
                }
            }

            session.History[i] = turn with
            {
                Content = Redact(turn.Content),
                ToolCalls = toolCalls
            };
        }
    }
}

public sealed class NoopRedactionPipeline : IRedactionPipeline
{
    public string Redact(string? value) => value ?? string.Empty;
    public void RedactSessionInPlace(Session session) { }
}

public sealed class BaselineSecretRedactor : ISensitiveDataRedactor
{
    private static readonly Regex BearerAuthorizationRegex = new(
        @"(?im)\b(Authorization\s*:\s*Bearer\s+)[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OpenAiSecretRegex = new(
        @"(?i)\bsk-[A-Za-z0-9_\-]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiKeyFieldRegex = new(
        @"(?i)(\bapi[_-]?key[""'\s:=]+)[A-Za-z0-9_\-]{12,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "baseline-secrets";

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var redacted = BearerAuthorizationRegex.Replace(value, "$1[REDACTED:authorization]");
        redacted = OpenAiSecretRegex.Replace(redacted, "[REDACTED:secret]");
        redacted = ApiKeyFieldRegex.Replace(redacted, "$1[REDACTED:secret]");
        return redacted;
    }
}
