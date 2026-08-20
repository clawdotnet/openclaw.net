namespace OpenClaw.StrategosWorkflowHost.Adapters;

// Pure function: pull the JSON document that EmitAuditTrace appended to
// ReviewState.ExecutionResult behind the literal "AuditTrace:" marker.
//
// EmitAuditTrace emits:
//   state.ExecutionResult = (state.ExecutionResult ?? "") + "\nAuditTrace:{json}"
//
// The parser:
//   1. Finds the last "AuditTrace:" marker (the most recent append wins).
//   2. Starts scanning at the first '{' after the marker.
//   3. Tracks brace depth so a nested object (future contributors) terminates correctly.
//   4. Returns the substring [start..end+1] or null when any step fails.
public static class EvidenceBundleParser
{
    private const string Marker = "AuditTrace:";

    public static string? ExtractAuditJson(string? executionResult)
    {
        if (string.IsNullOrEmpty(executionResult))
            return null;

        var markerIndex = executionResult.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var searchStart = markerIndex + Marker.Length;
        var openBrace = executionResult.IndexOf('{', searchStart);
        if (openBrace < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = openBrace; i < executionResult.Length; i++)
        {
            var c = executionResult[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return executionResult.Substring(openBrace, i - openBrace + 1);
            }
        }
        return null; // unbalanced braces; treat as no audit
    }
}