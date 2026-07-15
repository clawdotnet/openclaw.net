using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Actions;

internal static class ActionProposalBuilder
{
    internal static ActionProposalNormalizationResult Normalize(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return Fail("invalid_proposal", "Proposal output is empty.");

        try
        {
            var proposal = JsonSerializer.Deserialize(rawOutput, CoreJsonContext.Default.ActionProposal);
            if (proposal is null)
                return Fail("invalid_proposal", "Proposal payload is null.");

            if (ContainsBlockedDatabaseWrite(rawOutput, proposal))
                return Fail("policy_denied", "Direct database write path is blocked.");

            if (!ActionProposalValidation.TryValidate(proposal, out var errorCode))
                return Fail(errorCode, "Proposal payload failed validation.");

            return new ActionProposalNormalizationResult
            {
                Success = true,
                Proposal = proposal
            };
        }
        catch (JsonException ex)
        {
            return Fail("invalid_proposal", ex.Message);
        }
    }

    private static ActionProposalNormalizationResult Fail(string errorCode, string message)
        => new()
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = message
        };

    private static bool ContainsBlockedDatabaseWrite(string rawOutput, ActionProposal proposal)
    {
        if (HasBlockedMetadata(proposal.Metadata))
            return true;

        if (IsDirectDatabaseTarget(proposal.Target))
            return true;

        foreach (var step in proposal.Execution)
        {
            if (IsBlockedExecutionCall(step.Call))
                return true;

            if (HasBlockedSqlArgs(step.Args))
                return true;
        }

        // Keep a simple raw-text guard for obvious SQL write payloads outside the typed schema.
        return HasRawSqlWritePatterns(rawOutput);
    }

    private static bool HasBlockedMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var pair in metadata)
        {
            if (pair.Key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
                return true;

            if (pair.Value.Contains("server=", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsDirectDatabaseTarget(ActionProposalTarget target)
        => target.System.Equals("db", StringComparison.OrdinalIgnoreCase)
           || target.System.Equals("database", StringComparison.OrdinalIgnoreCase)
           || target.System.Equals("sql", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedExecutionCall(string call)
        => call.Contains("sql.execute", StringComparison.OrdinalIgnoreCase)
           || call.StartsWith("db.", StringComparison.OrdinalIgnoreCase)
           || call.StartsWith("database.", StringComparison.OrdinalIgnoreCase);

    private static bool HasBlockedSqlArgs(IReadOnlyDictionary<string, JsonElement> args)
    {
        foreach (var pair in args)
        {
            if (pair.Key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
                return true;

            if (pair.Key.Equals("sql", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("query", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("statement", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsSqlWriteVerb(ExtractValueText(pair.Value)))
                    return true;
            }
        }

        return false;
    }

    private static string ExtractValueText(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object => value.GetRawText(),
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };

    private static bool HasRawSqlWritePatterns(string rawOutput)
    {
        if (!rawOutput.Contains("sql", StringComparison.OrdinalIgnoreCase)
            && !rawOutput.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rawOutput.Contains("insert ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("update ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("delete ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("drop ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("alter ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("truncate ", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
               || rawOutput.Contains("server=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSqlWriteVerb(string value)
        => value.Contains("insert ", StringComparison.OrdinalIgnoreCase)
           || value.Contains("update ", StringComparison.OrdinalIgnoreCase)
           || value.Contains("delete ", StringComparison.OrdinalIgnoreCase)
           || value.Contains("drop ", StringComparison.OrdinalIgnoreCase)
           || value.Contains("alter ", StringComparison.OrdinalIgnoreCase)
           || value.Contains("truncate ", StringComparison.OrdinalIgnoreCase);
}
