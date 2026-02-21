using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw database plugin.
/// Executes SQL queries against SQLite, PostgreSQL, or MySQL databases.
/// Uses System.Data.Common (DbProviderFactory) for AOT-compatible database access.
/// Write operations (INSERT/UPDATE/DELETE/CREATE/DROP/ALTER) are gated behind AllowWrite.
/// </summary>
public sealed class DatabaseTool : ITool, IDisposable
{
    private readonly DatabaseConfig _config;
    private readonly ILogger? _logger;

    public DatabaseTool(DatabaseConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;

        if (config.AllowWrite)
            _logger?.LogWarning("DatabaseTool: AllowWrite is enabled. " +
                "The LLM can execute arbitrary write operations. " +
                "Connect with a read-only database user for safety.");
    }

    public string Name => "database";
    public string Description =>
        "Execute SQL queries against a database. " +
        "Supports SQLite, PostgreSQL, and MySQL. " +
        "Use for data retrieval, schema inspection, and (if enabled) data modification.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "description": "Action to perform",
              "enum": ["query", "execute", "schema", "tables"]
            },
            "sql": {
              "type": "string",
              "description": "SQL query or statement to execute"
            },
            "table": {
              "type": "string",
              "description": "Table name (for schema action)"
            }
          },
          "required": ["action"]
        }
        """;

    // Patterns that indicate write operations
    private static readonly string[] WriteKeywords =
        ["INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TRUNCATE", "MERGE", "REPLACE"];

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString()!.ToLowerInvariant();

        var connString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connString))
            return "Error: Database connection string not configured. Set Database.ConnectionString.";

        try
        {
            return action switch
            {
                "query" => await RunQueryAsync(args.RootElement, connString, ct),
                "execute" => await RunExecuteAsync(args.RootElement, connString, ct),
                "tables" => await ListTablesAsync(connString, ct),
                "schema" => await GetSchemaAsync(args.RootElement, connString, ct),
                _ => $"Error: Unsupported database action '{action}'. Use: query, execute, tables, schema."
            };
        }
        catch (DbException ex)
        {
            return $"Error: Database operation failed — {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: Database configuration issue — {ex.Message}";
        }
    }

    private async Task<string> RunQueryAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var sql = args.TryGetProperty("sql", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(sql))
            return "Error: 'sql' is required for query action.";

        // Block write operations through query action
        if (IsWriteOperation(sql))
            return "Error: Write operations must use the 'execute' action, not 'query'.";

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await FormatResultSetAsync(reader, ct);
    }

    private async Task<string> RunExecuteAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var sql = args.TryGetProperty("sql", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(sql))
            return "Error: 'sql' is required for execute action.";

        if (!_config.AllowWrite && IsWriteOperation(sql))
            return "Error: Write operations are disabled. Set Database.AllowWrite = true to enable.";

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        return $"Statement executed successfully. Rows affected: {rowsAffected}";
    }

    private async Task<string> ListTablesAsync(string connString, CancellationToken ct)
    {
        var sql = _config.Provider.ToLowerInvariant() switch
        {
            "sqlite" => "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",
            "postgres" => "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name",
            "mysql" => "SHOW TABLES",
            _ => "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name"
        };

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Tables:");
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            count++;
            sb.AppendLine($"  {reader.GetString(0)}");
        }

        if (count == 0)
            sb.AppendLine("  (no tables found)");

        return sb.ToString();
    }

    private async Task<string> GetSchemaAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var table = args.TryGetProperty("table", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(table))
            return "Error: 'table' is required for schema action.";

        var provider = _config.Provider.ToLowerInvariant();

        // Validate table name BEFORE opening connection for providers that need it
        if (provider is "sqlite" or "mysql")
        {
            if (!IsValidIdentifier(table))
                return "Error: Invalid table name. Only alphanumeric characters, underscores, and dots are allowed.";
        }

        // Use parameterized queries to prevent SQL injection in schema lookups
        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _config.TimeoutSeconds;

        if (provider == "sqlite")
        {
            cmd.CommandText = $"PRAGMA table_info('{table}')";
        }
        else if (provider == "mysql")
        {
            cmd.CommandText = $"DESCRIBE `{table}`";
        }
        else
        {
            // PostgreSQL and others: use parameterized query via information_schema
            cmd.CommandText = "SELECT column_name, data_type, is_nullable, column_default " +
                              "FROM information_schema.columns " +
                              "WHERE table_name = @tableName " +
                              "ORDER BY ordinal_position";
            var param = cmd.CreateParameter();
            param.ParameterName = "@tableName";
            param.Value = table;
            cmd.Parameters.Add(param);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await FormatResultSetAsync(reader, ct, $"Schema for table: {table}");
    }

    private async Task<string> FormatResultSetAsync(DbDataReader reader, CancellationToken ct, string? header = null)
    {
        var sb = new StringBuilder();
        if (header is not null)
            sb.AppendLine(header);

        var fieldCount = reader.FieldCount;
        if (fieldCount == 0)
            return header ?? "(no columns)";

        // Column headers
        var columns = new string[fieldCount];
        var widths = new int[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            columns[i] = reader.GetName(i);
            widths[i] = columns[i].Length;
        }

        // Read all rows (up to limit)
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct) && rows.Count < _config.MaxRows)
        {
            var row = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "NULL";
                widths[i] = Math.Max(widths[i], Math.Min(row[i].Length, 50));
            }
            rows.Add(row);
        }

        // Format as table
        var divider = new StringBuilder();
        for (var i = 0; i < fieldCount; i++)
        {
            if (i > 0) { sb.Append(" | "); divider.Append("-+-"); }
            sb.Append(columns[i].PadRight(widths[i]));
            divider.Append(new string('-', widths[i]));
        }
        sb.AppendLine();
        sb.AppendLine(divider.ToString());

        foreach (var row in rows)
        {
            for (var i = 0; i < fieldCount; i++)
            {
                if (i > 0) sb.Append(" | ");
                var val = row[i].Length > 50 ? row[i][..47] + "..." : row[i];
                sb.Append(val.PadRight(widths[i]));
            }
            sb.AppendLine();
        }

        sb.AppendLine($"\n({rows.Count} row{(rows.Count == 1 ? "" : "s")})");

        if (rows.Count == _config.MaxRows)
            sb.AppendLine($"(results limited to {_config.MaxRows} rows)");

        return sb.ToString();
    }

    private async Task<DbConnection> OpenConnectionAsync(string connString, CancellationToken ct)
    {
        var conn = CreateConnection(connString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Create a DbConnection based on the configured provider.
    /// For SQLite, uses Microsoft.Data.Sqlite which is AOT-friendly.
    /// For PostgreSQL and MySQL, requires the appropriate NuGet package.
    /// Falls back to a factory lookup.
    /// </summary>
    private DbConnection CreateConnection(string connString)
    {
        // Use provider factory pattern for extensibility.
        // The actual provider assembly must be referenced at build time or registered.
        var providerName = _config.Provider.ToLowerInvariant() switch
        {
            "sqlite" => "Microsoft.Data.Sqlite",
            "postgres" => "Npgsql",
            "mysql" => "MySqlConnector",
            _ => _config.Provider
        };

        if (!DbProviderFactories.TryGetFactory(providerName, out var factory))
        {
            // Try common type names as a fallback
            throw new InvalidOperationException(
                $"Database provider '{_config.Provider}' is not registered. " +
                $"Install the appropriate NuGet package " +
                $"(e.g., Microsoft.Data.Sqlite for SQLite, Npgsql for PostgreSQL, MySqlConnector for MySQL) " +
                $"and register via DbProviderFactories.RegisterFactory().");
        }

        var conn = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Failed to create connection for provider '{providerName}'.");
        conn.ConnectionString = connString;
        return conn;
    }

    private static bool IsWriteOperation(string sql)
    {
        var trimmed = sql.TrimStart();
        foreach (var keyword in WriteKeywords)
        {
            if (trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string? ResolveConnectionString() => SecretResolver.Resolve(_config.ConnectionString);

    /// <summary>
    /// Validates that a table name is a safe SQL identifier.
    /// Allows alphanumeric, underscores, dots (schema.table), and hyphens.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return false;

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                return false;
        }
        return true;
    }

    public void Dispose() { }
}
