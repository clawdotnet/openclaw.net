using System.Text;
using System.Text.Json;
using MemPalace.KnowledgeGraph;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Gateway.Tools;

internal sealed class MempalaceKnowledgeGraphTool : ITool
{
    private readonly IKnowledgeGraph _knowledgeGraph;

    public MempalaceKnowledgeGraphTool(IKnowledgeGraph knowledgeGraph)
        => _knowledgeGraph = knowledgeGraph;

    public string Name => "mempalace_kg";

    public string Description =>
        "Read and write MemPalace temporal knowledge graph relationships with validity windows.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["add","query","timeline"] },
            "subject": { "type": "string", "description": "Subject entity as type:id for add/query" },
            "predicate": { "type": "string", "description": "Relationship predicate for add/query" },
            "object": { "type": "string", "description": "Object entity as type:id for add/query" },
            "entity": { "type": "string", "description": "Entity as type:id for timeline" },
            "at": { "type": "string", "description": "Optional ISO8601 point-in-time query" },
            "from": { "type": "string", "description": "Optional ISO8601 timeline start" },
            "to": { "type": "string", "description": "Optional ISO8601 timeline end" }
          },
          "required": ["action"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;
        var action = ReadString(root, "action");
        return action switch
        {
            "add" => await AddAsync(root, ct),
            "query" => await QueryAsync(root, ct),
            "timeline" => await TimelineAsync(root, ct),
            _ => "Error: action must be one of add, query, or timeline."
        };
    }

    private async ValueTask<string> AddAsync(JsonElement root, CancellationToken ct)
    {
        if (!TryReadEntity(root, "subject", required: true, out var subject, out var entityError))
            return entityError;
        var predicate = ReadString(root, "predicate");
        if (!TryReadEntity(root, "object", required: true, out var obj, out entityError))
            return entityError;
        if (subject is null || obj is null || string.IsNullOrWhiteSpace(predicate))
            return "Error: add requires subject, predicate, and object.";

        var now = DateTimeOffset.UtcNow;
        var validFrom = ReadDate(root, "at") ?? now;
        var id = await _knowledgeGraph.AddAsync(
            new TemporalTriple(
                new Triple(subject, predicate, obj),
                validFrom,
                null,
                now),
            ct);

        return $"Added temporal triple {id}: {subject} {predicate} {obj}";
    }

    private async ValueTask<string> QueryAsync(JsonElement root, CancellationToken ct)
    {
        if (!TryReadEntity(root, "subject", required: false, out var subject, out var entityError))
            return entityError;
        if (!TryReadEntity(root, "object", required: false, out var obj, out entityError))
            return entityError;

        var pattern = new TriplePattern(
            subject,
            ReadString(root, "predicate"),
            obj);
        var at = ReadDate(root, "at");
        var results = await _knowledgeGraph.QueryAsync(pattern, at, ct);
        if (results.Count == 0)
            return "No matching temporal knowledge graph triples found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Matches: {results.Count}");
        foreach (var item in results)
        {
            sb.Append(item.Triple.Subject)
                .Append(' ')
                .Append(item.Triple.Predicate)
                .Append(' ')
                .Append(item.Triple.Object)
                .Append(" [")
                .Append(item.ValidFrom.ToString("O"));
            if (item.ValidTo is { } validTo)
                sb.Append(" - ").Append(validTo.ToString("O"));
            sb.AppendLine("]");
        }

        return sb.ToString().TrimEnd();
    }

    private async ValueTask<string> TimelineAsync(JsonElement root, CancellationToken ct)
    {
        if (!TryReadEntity(root, "entity", required: true, out var entity, out var entityError))
            return entityError;
        if (entity is null)
            return "Error: timeline requires entity.";

        var events = await _knowledgeGraph.TimelineAsync(
            entity,
            ReadDate(root, "from"),
            ReadDate(root, "to"),
            ct);
        if (events.Count == 0)
            return "No temporal knowledge graph events found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Events: {events.Count}");
        foreach (var item in events)
        {
            var arrow = string.Equals(item.Direction, "outgoing", StringComparison.OrdinalIgnoreCase) ? "->" : "<-";
            sb.Append(item.At.ToString("O"))
                .Append(' ')
                .Append(arrow)
                .Append(' ')
                .Append(item.Predicate)
                .Append(' ')
                .AppendLine(item.Other.ToString());
        }

        return sb.ToString().TrimEnd();
    }

    private static bool TryReadEntity(
        JsonElement root,
        string propertyName,
        bool required,
        out EntityRef? entity,
        out string error)
    {
        entity = null;
        error = string.Empty;
        var value = ReadString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                error = $"Error: {propertyName} is required and must use type:id format.";
                return false;
            }

            return true;
        }

        try
        {
            entity = EntityRef.Parse(value);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Error: {propertyName} must use type:id format. {ex.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string propertyName)
        => DateTimeOffset.TryParse(ReadString(root, propertyName), out var value) ? value : null;
}
