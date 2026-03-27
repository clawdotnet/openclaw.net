using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Http;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

internal sealed class NotionApiClient : IDisposable
{
    private const int MaxRichTextSegmentLength = 1800;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly NotionConfig _config;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly HashSet<string> _allowedPageIds;
    private readonly HashSet<string> _allowedDatabaseIds;

    public NotionApiClient(NotionConfig config, HttpClient? httpClient = null)
    {
        _config = config;

        if (!Uri.TryCreate(config.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid Notion BaseUrl: {config.BaseUrl}", nameof(config));

        _baseUri = baseUri;
        _http = httpClient ?? HttpClientFactory.Create();

        var token = SecretResolver.Resolve(config.ApiKeyRef);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Notion token not configured. Set OpenClaw:Plugins:Native:Notion:ApiKeyRef (default env:NOTION_API_KEY).");
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Notion-Version", config.ApiVersion);

        _allowedPageIds = new HashSet<string>(StringComparer.Ordinal);
        _allowedDatabaseIds = new HashSet<string>(StringComparer.Ordinal);
        AddAllowedId(_allowedPageIds, config.DefaultPageId);
        AddAllowedId(_allowedDatabaseIds, config.DefaultDatabaseId);
        foreach (var pageId in config.AllowedPageIds)
            AddAllowedId(_allowedPageIds, pageId);
        foreach (var databaseId in config.AllowedDatabaseIds)
            AddAllowedId(_allowedDatabaseIds, databaseId);
    }

    public string? DefaultPageId => _config.DefaultPageId;
    public string? DefaultDatabaseId => _config.DefaultDatabaseId;
    public int MaxSearchResults => Math.Max(1, _config.MaxSearchResults);

    public bool IsPageExplicitlyAllowed(string pageId) => _allowedPageIds.Contains(NormalizeId(pageId));

    public bool IsDatabaseExplicitlyAllowed(string databaseId) => _allowedDatabaseIds.Contains(NormalizeId(databaseId));

    public string RequireDefaultPageId()
    {
        if (string.IsNullOrWhiteSpace(_config.DefaultPageId))
            throw new InvalidOperationException("Notion default page not configured. Set OpenClaw:Plugins:Native:Notion:DefaultPageId.");

        return _config.DefaultPageId;
    }

    public string RequireAllowedDatabaseId(string? databaseId)
    {
        var resolved = string.IsNullOrWhiteSpace(databaseId) ? _config.DefaultDatabaseId : databaseId;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                "Notion database id is required. Set OpenClaw:Plugins:Native:Notion:DefaultDatabaseId or pass database_id.");
        }

        if (!IsDatabaseExplicitlyAllowed(resolved))
        {
            throw new InvalidOperationException(
                $"Database '{resolved}' is not allowed. Configure Plugins.Native.Notion.AllowedDatabaseIds or DefaultDatabaseId.");
        }

        return resolved;
    }

    public async Task<NotionPage> GetAccessiblePageAsync(string pageId, CancellationToken ct)
    {
        var page = await GetPageAsync(pageId, ct);
        if (IsPageExplicitlyAllowed(page.Id))
            return page;

        if (!string.IsNullOrWhiteSpace(page.ParentDatabaseId) && IsDatabaseExplicitlyAllowed(page.ParentDatabaseId))
            return page;

        throw new InvalidOperationException(
            $"Page '{pageId}' is not allowed. Configure Plugins.Native.Notion.AllowedPageIds or AllowedDatabaseIds.");
    }

    public async Task<NotionPageContent> ReadPageAsync(string pageId, CancellationToken ct)
    {
        var page = await GetAccessiblePageAsync(pageId, ct);
        var body = await ReadBlockChildrenTextAsync(page.Id, ct);
        return new NotionPageContent(page, body);
    }

    public async Task<IReadOnlyList<NotionNoteSummary>> ListNotesAsync(string databaseId, int limit, CancellationToken ct)
    {
        var allowedDatabaseId = RequireAllowedDatabaseId(databaseId);
        var request = new NotionDatabaseQueryRequest { PageSize = ClampLimit(limit) };
        using var body = CreateJsonContent(request, NotionJsonContext.Default.NotionDatabaseQueryRequest);
        using var doc = await SendForJsonAsync(HttpMethod.Post,
            $"/databases/{Uri.EscapeDataString(allowedDatabaseId)}/query",
            body, ct);

        return ParsePageResults(doc.RootElement)
            .Where(page => !string.IsNullOrWhiteSpace(page.ParentDatabaseId) &&
                           string.Equals(NormalizeId(page.ParentDatabaseId!), NormalizeId(allowedDatabaseId), StringComparison.Ordinal))
            .Take(ClampLimit(limit))
            .ToArray();
    }

    public async Task<IReadOnlyList<NotionNoteSummary>> SearchAsync(string query, string? databaseId, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.", nameof(query));

        string? scopedDatabaseId = null;
        if (!string.IsNullOrWhiteSpace(databaseId))
            scopedDatabaseId = RequireAllowedDatabaseId(databaseId);

        var request = new NotionSearchRequest
        {
            Query = query,
            PageSize = ClampLimit(limit),
            Filter = new NotionSearchFilter
            {
                Property = "object",
                Value = "page"
            }
        };

        using var body = CreateJsonContent(request, NotionJsonContext.Default.NotionSearchRequest);
        using var doc = await SendForJsonAsync(HttpMethod.Post, "/search", body, ct);

        return ParsePageResults(doc.RootElement)
            .Where(page => IsPageExplicitlyAllowed(page.PageId) ||
                           (!string.IsNullOrWhiteSpace(page.ParentDatabaseId) && IsDatabaseExplicitlyAllowed(page.ParentDatabaseId)))
            .Where(page => scopedDatabaseId is null ||
                           (!string.IsNullOrWhiteSpace(page.ParentDatabaseId) &&
                            string.Equals(NormalizeId(page.ParentDatabaseId!), NormalizeId(scopedDatabaseId!), StringComparison.Ordinal)))
            .Take(ClampLimit(limit))
            .ToArray();
    }

    public async Task AppendPageAsync(string pageId, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content is required.", nameof(content));

        var page = await GetAccessiblePageAsync(pageId, ct);
        using var body = CreateJsonContent(BuildAppendChildrenPayload(content));
        await SendWithoutBodyAsync(HttpMethod.Patch,
            $"/blocks/{Uri.EscapeDataString(page.Id)}/children",
            body,
            ct);
    }

    public async Task<NotionNoteSummary> CreateNoteAsync(
        string databaseId,
        string title,
        string content,
        IReadOnlyList<string> tags,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required.", nameof(title));

        var allowedDatabaseId = RequireAllowedDatabaseId(databaseId);
        var schema = await GetDatabaseSchemaAsync(allowedDatabaseId, ct);
        using var body = CreateJsonContent(BuildCreatePagePayload(schema, title, content, tags));
        using var doc = await SendForJsonAsync(HttpMethod.Post, "/pages", body, ct);
        return ParsePage(doc.RootElement);
    }

    public async Task<NotionNoteSummary> UpdateNoteAsync(
        string pageId,
        string? title,
        string? content,
        bool append,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Either title or content must be provided.");

        var page = await GetAccessiblePageAsync(pageId, ct);

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (string.IsNullOrWhiteSpace(page.ParentDatabaseId))
            {
                throw new InvalidOperationException(
                    "Title updates are only supported for database-backed notes.");
            }

            var schema = await GetDatabaseSchemaAsync(page.ParentDatabaseId, ct);
            using var body = CreateJsonContent(BuildUpdatePageTitlePayload(schema, title));
            using var doc = await SendForJsonAsync(HttpMethod.Patch,
                $"/pages/{Uri.EscapeDataString(page.Id)}",
                body, ct);
            page = ParsePage(doc.RootElement).Page;
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            if (!append)
                await ReplacePageContentAsync(page.Id, content, ct);
            else
                await AppendPageAsync(page.Id, content, ct);
        }

        using var finalDoc = await SendForJsonAsync(HttpMethod.Get, $"/pages/{Uri.EscapeDataString(page.Id)}", null, ct);
        return ParsePage(finalDoc.RootElement);
    }

    public void Dispose() => _http.Dispose();

    private async Task ReplacePageContentAsync(string pageId, string content, CancellationToken ct)
    {
        var childIds = await ListBlockIdsAsync(pageId, ct);
        foreach (var childId in childIds)
        {
            using var archiveBody = CreateJsonContent("""{"archived":true}""");
            await SendWithoutBodyAsync(HttpMethod.Patch,
                $"/blocks/{Uri.EscapeDataString(childId)}",
                archiveBody,
                ct);
        }

        await AppendPageAsync(pageId, content, ct);
    }

    private async Task<NotionPage> GetPageAsync(string pageId, CancellationToken ct)
    {
        using var doc = await SendForJsonAsync(HttpMethod.Get, $"/pages/{Uri.EscapeDataString(pageId)}", null, ct);
        return ParsePage(doc.RootElement).Page;
    }

    private async Task<NotionDatabaseSchema> GetDatabaseSchemaAsync(string databaseId, CancellationToken ct)
    {
        using var doc = await SendForJsonAsync(HttpMethod.Get, $"/databases/{Uri.EscapeDataString(databaseId)}", null, ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Notion database '{databaseId}' did not include a properties object.");

        string? titlePropertyName = null;
        string? tagsPropertyName = null;

        foreach (var property in properties.EnumerateObject())
        {
            var type = property.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (string.Equals(type, "title", StringComparison.OrdinalIgnoreCase))
                titlePropertyName ??= property.Name;

            if (string.Equals(type, "multi_select", StringComparison.OrdinalIgnoreCase))
            {
                if (tagsPropertyName is null || property.Name.Equals("tags", StringComparison.OrdinalIgnoreCase))
                    tagsPropertyName = property.Name;
            }
        }

        if (titlePropertyName is null)
            throw new InvalidOperationException($"Notion database '{databaseId}' does not define a title property.");

        return new NotionDatabaseSchema(databaseId, titlePropertyName, tagsPropertyName);
    }

    private async Task<string> ReadBlockChildrenTextAsync(string blockId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? cursor = null;

        do
        {
            var path = $"/blocks/{Uri.EscapeDataString(blockId)}/children?page_size=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&start_cursor={Uri.EscapeDataString(cursor)}";

            using var doc = await SendForJsonAsync(HttpMethod.Get, path, null, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in results.EnumerateArray())
                    AppendBlockText(block, sb);
            }

            cursor = root.TryGetProperty("has_more", out var hasMoreElement) &&
                     hasMoreElement.ValueKind == JsonValueKind.True &&
                     root.TryGetProperty("next_cursor", out var nextCursor)
                ? nextCursor.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return sb.ToString().Trim();
    }

    private async Task<IReadOnlyList<string>> ListBlockIdsAsync(string blockId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? cursor = null;

        do
        {
            var path = $"/blocks/{Uri.EscapeDataString(blockId)}/children?page_size=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&start_cursor={Uri.EscapeDataString(cursor)}";

            using var doc = await SendForJsonAsync(HttpMethod.Get, path, null, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in results.EnumerateArray())
                {
                    if (block.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                    {
                        var id = idElement.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            ids.Add(id);
                    }
                }
            }

            cursor = root.TryGetProperty("has_more", out var hasMoreElement) &&
                     hasMoreElement.ValueKind == JsonValueKind.True &&
                     root.TryGetProperty("next_cursor", out var nextCursor)
                ? nextCursor.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return ids;
    }

    private static void AppendBlockText(JsonElement block, StringBuilder sb)
    {
        var type = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(type))
            return;

        var text = TryReadRichTextFromBlock(block, type);
        if (string.IsNullOrWhiteSpace(text))
            return;

        switch (type)
        {
            case "heading_1":
                sb.Append("# ").AppendLine(text).AppendLine();
                break;
            case "heading_2":
                sb.Append("## ").AppendLine(text).AppendLine();
                break;
            case "heading_3":
                sb.Append("### ").AppendLine(text).AppendLine();
                break;
            case "bulleted_list_item":
                sb.Append("- ").AppendLine(text);
                break;
            case "numbered_list_item":
                sb.Append("1. ").AppendLine(text);
                break;
            case "to_do":
                var checkedValue = block.GetProperty(type).TryGetProperty("checked", out var checkedElement) &&
                                   checkedElement.ValueKind == JsonValueKind.True;
                sb.Append(checkedValue ? "- [x] " : "- [ ] ").AppendLine(text);
                break;
            case "quote":
                sb.Append("> ").AppendLine(text).AppendLine();
                break;
            case "code":
                var language = block.GetProperty(type).TryGetProperty("language", out var languageElement)
                    ? languageElement.GetString()
                    : "";
                sb.Append("```").Append(language).AppendLine();
                sb.AppendLine(text);
                sb.AppendLine("```").AppendLine();
                break;
            case "callout":
                sb.Append("Note: ").AppendLine(text).AppendLine();
                break;
            default:
                sb.AppendLine(text).AppendLine();
                break;
        }
    }

    private static string? TryReadRichTextFromBlock(JsonElement block, string type)
    {
        if (!block.TryGetProperty(type, out var typedBlock) || typedBlock.ValueKind != JsonValueKind.Object)
            return null;

        if (!typedBlock.TryGetProperty("rich_text", out var richText) || richText.ValueKind != JsonValueKind.Array)
            return null;

        return FlattenRichText(richText);
    }

    private static string FlattenRichText(JsonElement richTextArray)
    {
        var sb = new StringBuilder();
        foreach (var item in richTextArray.EnumerateArray())
        {
            if (item.TryGetProperty("plain_text", out var plainText) && plainText.ValueKind == JsonValueKind.String)
            {
                sb.Append(plainText.GetString());
            }
            else if (item.TryGetProperty("text", out var text) &&
                     text.ValueKind == JsonValueKind.Object &&
                     text.TryGetProperty("content", out var content) &&
                     content.ValueKind == JsonValueKind.String)
            {
                sb.Append(content.GetString());
            }
        }

        return sb.ToString().Trim();
    }

    private static JsonContent CreateJsonContent<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
        => JsonContent.Create(value, jsonTypeInfo);

    private static StringContent CreateJsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static string BuildAppendChildrenPayload(string content)
    {
        var blocks = BuildBlocks(content);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            foreach (var block in blocks)
                WriteParagraphBlock(writer, block.Type, block.Content);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildCreatePagePayload(
        NotionDatabaseSchema schema,
        string title,
        string content,
        IReadOnlyList<string> tags)
    {
        var blocks = BuildBlocks(content);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("parent");
            writer.WriteStartObject();
            writer.WriteString("database_id", schema.DatabaseId);
            writer.WriteEndObject();

            writer.WritePropertyName("properties");
            writer.WriteStartObject();

            writer.WritePropertyName(schema.TitlePropertyName);
            writer.WriteStartObject();
            writer.WritePropertyName("title");
            WriteRichTextArray(writer, title);
            writer.WriteEndObject();

            if (!string.IsNullOrWhiteSpace(schema.TagsPropertyName) && tags.Count > 0)
            {
                writer.WritePropertyName(schema.TagsPropertyName);
                writer.WriteStartObject();
                writer.WritePropertyName("multi_select");
                writer.WriteStartArray();
                foreach (var tag in tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tag.Trim());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WritePropertyName("children");
            writer.WriteStartArray();
            foreach (var block in blocks)
                WriteParagraphBlock(writer, block.Type, block.Content);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildUpdatePageTitlePayload(NotionDatabaseSchema schema, string title)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName(schema.TitlePropertyName);
            writer.WriteStartObject();
            writer.WritePropertyName("title");
            WriteRichTextArray(writer, title);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<NotionBlockInput> BuildBlocks(string content)
    {
        var blocks = new List<NotionBlockInput>();
        if (string.IsNullOrWhiteSpace(content))
            return blocks;

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var (type, text) = line switch
            {
                _ when line.StartsWith("- [ ] ", StringComparison.Ordinal) => ("to_do_open", line[6..]),
                _ when line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase) => ("to_do_done", line[6..]),
                _ when line.StartsWith("- ", StringComparison.Ordinal) => ("bulleted_list_item", line[2..]),
                _ when line.StartsWith("* ", StringComparison.Ordinal) => ("bulleted_list_item", line[2..]),
                _ when IsNumberedLine(line, out var numberedText) => ("numbered_list_item", numberedText),
                _ when line.StartsWith("> ", StringComparison.Ordinal) => ("quote", line[2..]),
                _ => ("paragraph", line)
            };

            foreach (var chunk in ChunkText(text))
                blocks.Add(new NotionBlockInput(type, chunk));
        }

        if (blocks.Count == 0)
            blocks.Add(new NotionBlockInput("paragraph", "(empty)"));

        return blocks;
    }

    private static bool IsNumberedLine(string line, out string text)
    {
        text = "";
        var dotIndex = line.IndexOf(". ", StringComparison.Ordinal);
        if (dotIndex <= 0)
            return false;

        for (var i = 0; i < dotIndex; i++)
        {
            if (!char.IsDigit(line[i]))
                return false;
        }

        text = line[(dotIndex + 2)..];
        return text.Length > 0;
    }

    private static IReadOnlyList<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > MaxRichTextSegmentLength)
        {
            chunks.Add(remaining[..MaxRichTextSegmentLength]);
            remaining = remaining[MaxRichTextSegmentLength..];
        }

        if (!string.IsNullOrWhiteSpace(remaining))
            chunks.Add(remaining);

        return chunks.Count == 0 ? [""] : chunks;
    }

    private static void WriteParagraphBlock(Utf8JsonWriter writer, string type, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("object", "block");

        var notionType = type switch
        {
            "to_do_open" or "to_do_done" => "to_do",
            _ => type
        };

        writer.WriteString("type", notionType);
        writer.WritePropertyName(notionType);
        writer.WriteStartObject();

        if (notionType == "to_do")
            writer.WriteBoolean("checked", string.Equals(type, "to_do_done", StringComparison.Ordinal));

        writer.WritePropertyName("rich_text");
        WriteRichTextArray(writer, content);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRichTextArray(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartArray();
        foreach (var chunk in ChunkText(text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WritePropertyName("text");
            writer.WriteStartObject();
            writer.WriteString("content", chunk);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void AddAllowedId(HashSet<string> set, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            set.Add(NormalizeId(id));
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, 100);

    private static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id is required.", nameof(id));

        return new string(id.Where(static ch => ch != '-').ToArray()).Trim().ToLowerInvariant();
    }

    private static NotionNoteSummary ParsePage(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
        var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        var lastEdited = root.TryGetProperty("last_edited_time", out var lastEditedElement) && lastEditedElement.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(lastEditedElement.GetString(), out var parsedLastEdited)
            ? parsedLastEdited
            : (DateTimeOffset?)null;

        string? parentDatabaseId = null;
        if (root.TryGetProperty("parent", out var parentElement) && parentElement.ValueKind == JsonValueKind.Object)
        {
            if (parentElement.TryGetProperty("database_id", out var databaseId) && databaseId.ValueKind == JsonValueKind.String)
                parentDatabaseId = databaseId.GetString();
            else if (parentElement.TryGetProperty("data_source_id", out var dataSourceId) && dataSourceId.ValueKind == JsonValueKind.String)
                parentDatabaseId = dataSourceId.GetString();
        }

        var title = TryExtractTitle(root);
        var page = new NotionPage(id, title, parentDatabaseId, lastEdited, url);
        return new NotionNoteSummary(page, title);
    }

    private static IEnumerable<NotionNoteSummary> ParsePageResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("object", out var objectElement) &&
                objectElement.ValueKind == JsonValueKind.String &&
                string.Equals(objectElement.GetString(), "page", StringComparison.Ordinal))
            {
                yield return ParsePage(item);
            }
        }
    }

    private static string TryExtractTitle(JsonElement page)
    {
        if (!page.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return "(untitled)";

        foreach (var property in properties.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("type", out var typeElement))
                continue;

            var type = typeElement.GetString();
            if (string.Equals(type, "title", StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetProperty("title", out var titleArray) &&
                titleArray.ValueKind == JsonValueKind.Array)
            {
                var text = FlattenRichText(titleArray);
                return string.IsNullOrWhiteSpace(text) ? "(untitled)" : text;
            }
        }

        return "(untitled)";
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Content = content;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync(path, response, cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
    }

    private async Task SendWithoutBodyAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Content = content;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync(path, response, cts.Token);
    }

    private static async Task<Exception> CreateRequestExceptionAsync(
        string path,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parsed = JsonSerializer.Deserialize(text, NotionJsonContext.Default.NotionApiErrorResponse);
                detail = parsed?.Message ?? text;
            }
        }
        catch
        {
            detail = null;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Notion authorization failed (401/403). Check the integration token and confirm the target page/database is shared with the integration.",
            HttpStatusCode.NotFound =>
                $"Notion resource '{path}' was not found. Check the page/database id and confirm the integration has access.",
            HttpStatusCode.TooManyRequests =>
                "Notion rate limited the request (429). Retry later or reduce request volume.",
            _ when (int)response.StatusCode >= 500 =>
                $"Notion service returned {(int)response.StatusCode} {response.ReasonPhrase}. Retry later.",
            _ =>
                $"Notion request failed: {(int)response.StatusCode} {response.ReasonPhrase}."
        };

        if (!string.IsNullOrWhiteSpace(detail))
            message += $" Details: {detail}";

        return new HttpRequestException(message);
    }
}

internal sealed record NotionPage(
    string Id,
    string Title,
    string? ParentDatabaseId,
    DateTimeOffset? LastEditedAt,
    string? Url);

internal sealed record NotionPageContent(NotionPage Page, string Body);

internal sealed record NotionNoteSummary(NotionPage Page, string Title)
{
    public string PageId => Page.Id;
    public string? ParentDatabaseId => Page.ParentDatabaseId;
    public DateTimeOffset? LastEditedAt => Page.LastEditedAt;
    public string? Url => Page.Url;
}

internal sealed record NotionDatabaseSchema(string DatabaseId, string TitlePropertyName, string? TagsPropertyName);

internal sealed record NotionBlockInput(string Type, string Content);

internal sealed class NotionDatabaseQueryRequest
{
    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }
}

internal sealed class NotionSearchRequest
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = "";

    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }

    [JsonPropertyName("filter")]
    public NotionSearchFilter? Filter { get; init; }
}

internal sealed class NotionSearchFilter
{
    [JsonPropertyName("property")]
    public string Property { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}

internal sealed class NotionApiErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NotionDatabaseQueryRequest))]
[JsonSerializable(typeof(NotionSearchRequest))]
[JsonSerializable(typeof(NotionSearchFilter))]
[JsonSerializable(typeof(NotionApiErrorResponse))]
internal partial class NotionJsonContext : JsonSerializerContext;
