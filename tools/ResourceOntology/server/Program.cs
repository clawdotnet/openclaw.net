using ResourceOntology.Api.Models;
using ResourceOntology.Api.Services;
using VDS.RDF.JsonLd;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;
using Newtonsoft.Json.Linq;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.Services.AddSingleton<OntologyParser>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Resource Ontology API",
        Version = "v1",
        Description = "Parse and explore OWL ontologies."
    });
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors("dev");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve the built Svelte SPA (client/dist copied into wwwroot) in production.
app.UseDefaultFiles();
app.UseStaticFiles();

var parser = app.Services.GetRequiredService<OntologyParser>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var ontologyCache = new System.Collections.Concurrent.ConcurrentDictionary<string, OntologyDto>();
 
string? ResolveOntologyDir()
{
    var candidates = new[]
    {
        Path.Combine(app.Environment.ContentRootPath, "..", "ontology"),
        Path.Combine(app.Environment.ContentRootPath, "ontology"),
    };
    foreach (var c in candidates)
        if (Directory.Exists(c))
            return Path.GetFullPath(c);
    return null;
}

// (Removed deprecated default/source endpoints and helpers)

// Parse an uploaded ontology. Accepts the raw OWL/RDF-XML as the request body.
app.MapPost("/api/ontology/parse", async (HttpRequest request) =>
{
    string name = request.Query["name"].FirstOrDefault() ?? "uploaded.owl";
    try
    {
        if (request.HasFormContentType && request.Form.Files.Count > 0)
        {
            var file = request.Form.Files[0];
            name = file.FileName;
            using var stream = file.OpenReadStream();
            using var sr = new StreamReader(stream);
            return Results.Ok(parser.Parse(sr, name));
        }

        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { error = "Request body was empty." });
        using var sr2 = new StringReader(text);
        return Results.Ok(parser.Parse(sr2, name));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to parse uploaded ontology {Name}", name);
        return Results.BadRequest(new { error = $"Could not parse '{name}': {ex.Message}" });
    }
}).Produces<OntologyDto>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest);

app.MapGet("/api/health", () => Results.Ok(new HealthDto { Status = "ok" }));

app.MapGet("/api/ontology/files", () =>
{
    var dir = ResolveOntologyDir();
    if (dir == null)
        return Results.Ok(new OntologyFileList());

    var list = new OntologyFileList();
    list.Files = Directory.GetFiles(dir, "*.owl")
        .Select(f => new OntologyFileEntry
        {
            Name = Path.GetFileName(f),
            DisplayName = Path.GetFileNameWithoutExtension(f)
        })
        .OrderBy(f => f.DisplayName)
        .ToList();

    return Results.Ok(list);
});

app.MapGet("/api/ontology/load", (string file) =>
{
    if (string.IsNullOrWhiteSpace(file) || file.Contains("..") || file.Contains('/') || file.Contains('\\'))
        return Results.BadRequest(new { error = "Invalid file name." });

    if (ontologyCache.TryGetValue(file, out var cached))
        return Results.Ok(cached);

    var dir = ResolveOntologyDir();
    if (dir == null)
        return Results.NotFound(new { error = "Ontology directory not found." });

    var path = Path.Combine(dir, file);
    if (!File.Exists(path))
        return Results.NotFound(new { error = $"File '{file}' not found." });

    try
    {
        logger.LogInformation("Parsing ontology: {Path}", path);
        var dto = parser.ParseFile(path);
        ontologyCache[file] = dto;
        return Results.Ok(dto);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to parse ontology {File}", file);
        return Results.BadRequest(new { error = $"Could not parse '{file}': {ex.Message}" });
    }
}).Produces<OntologyDto>(StatusCodes.Status200OK)
  .Produces(StatusCodes.Status400BadRequest)
  .Produces(StatusCodes.Status404NotFound);

app.MapGet("/api/ontology/export-jsonld", (string file, string format, bool download = false) =>
{
    if (string.IsNullOrWhiteSpace(file) || file.Contains("..") || file.Contains('/') || file.Contains('\\'))
        return Results.BadRequest(new { error = "Invalid file name." });

    var dir = ResolveOntologyDir();
    if (dir == null)
        return Results.NotFound(new { error = "Ontology directory not found." });

    var path = Path.Combine(dir, file);
    if (!File.Exists(path))
        return Results.NotFound(new { error = $"File '{file}' not found." });

    format = (format?.ToLowerInvariant()) switch
    {
        "expanded" => "expanded",
        _ => "compact"
    };

    try
    {
        logger.LogInformation("Exporting JSON-LD ({Format}): {Path}", format, path);

        // Load RDF/XML into a Graph
        var graph = new VDS.RDF.Graph();
        var parser = new RdfXmlParser();
        using (var reader = new StreamReader(File.OpenRead(path)))
        {
            parser.Load(graph, reader);
        }

        // Create a TripleStore with the loaded graph
        var store = new VDS.RDF.TripleStore();
        store.Add(graph);

        // Write to JSON-LD
        var options = new JsonLdWriterOptions { UseNativeTypes = true };
        var writer = new JsonLdWriter(options);
        
        using var sw = new System.IO.StringWriter();
        writer.Save(store, sw);
        var jsonld = sw.ToString();

        // Decode percent-encoded IRIs for readability (e.g., Chinese characters)
        jsonld = DecodeIrisInJsonLd(jsonld);

        // Apply format conversion if expanded format is requested
        if (format == "expanded")
        {
            try
            {
                var jObj = JObject.Parse(jsonld);
                var expanded = VDS.RDF.JsonLd.JsonLdProcessor.Expand(jObj, new VDS.RDF.JsonLd.JsonLdProcessorOptions());
                jsonld = expanded.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception expandEx)
            {
                logger.LogWarning(expandEx, "Failed to expand JSON-LD, falling back to compact format");
                // If expansion fails, keep the compact format
            }
        }

        if (download)
        {
            var downloadName = $"{Path.GetFileNameWithoutExtension(file)}.jsonld";
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(jsonld),
                "application/ld+json",
                downloadName
            );
        }

        return Results.Text(jsonld, "text/plain", System.Text.Encoding.UTF8);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to export JSON-LD for {File}", file);
        return Results.BadRequest(new { error = $"Export failed: {ex.Message}" });
    }
});

// POST /api/ontology/export-jsonld: Upload raw OWL/RDF-XML and export as JSON-LD
app.MapPost("/api/ontology/export-jsonld", async (HttpRequest request, string format, string fileName) =>
{
    if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        return Results.BadRequest(new { error = "Invalid file name." });

    var dir = ResolveOntologyDir();
    if (dir == null)
        return Results.NotFound(new { error = "Ontology directory not found." });

    format = (format?.ToLowerInvariant()) switch
    {
        "expanded" => "expanded",
        _ => "compact"
    };

    try
    {
        // Read OWL/RDF-XML from request body
        using var reader = new StreamReader(request.Body);
        var owlText = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(owlText))
            return Results.BadRequest(new { error = "Request body was empty." });

        // Save to ontology/ directory (handle name conflicts with timestamp suffix)
        var savePath = Path.Combine(dir, fileName);
        if (File.Exists(savePath))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            savePath = Path.Combine(dir, $"{nameWithoutExt}_{timestamp}{ext}");
        }
        await File.WriteAllTextAsync(savePath, owlText);
        logger.LogInformation("Saved uploaded ontology: {Path}", savePath);

        // Convert to JSON-LD using same approach as GET endpoint
        var graph = new VDS.RDF.Graph();
        var rdfParser = new RdfXmlParser();
        using (var sr = new StringReader(owlText))
        {
            rdfParser.Load(graph, sr);
        }

        // Create a TripleStore with the loaded graph
        var store = new VDS.RDF.TripleStore();
        store.Add(graph);

        // Write to JSON-LD
        var options = new JsonLdWriterOptions { UseNativeTypes = true };
        var writer = new JsonLdWriter(options);
        
        using var sw = new System.IO.StringWriter();
        writer.Save(store, sw);
        var jsonld = sw.ToString();

        // Decode percent-encoded IRIs for readability (e.g., Chinese characters)
        jsonld = DecodeIrisInJsonLd(jsonld);

        // Apply format conversion if expanded format is requested
        if (format == "expanded")
        {
            try
            {
                var jObj = JObject.Parse(jsonld);
                var expanded = VDS.RDF.JsonLd.JsonLdProcessor.Expand(jObj, new VDS.RDF.JsonLd.JsonLdProcessorOptions());
                jsonld = expanded.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception expandEx)
            {
                logger.LogWarning(expandEx, "Failed to expand JSON-LD, falling back to compact format");
                // If expansion fails, keep the compact format
            }
        }

        return Results.Text(jsonld, "text/plain", System.Text.Encoding.UTF8);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to export uploaded JSON-LD for {File}", fileName);
        return Results.BadRequest(new { error = $"Export failed: {ex.Message}" });
    }
});

// SPA fallback: any non-API route returns index.html so client-side routing works.
app.MapFallbackToFile("index.html");

// ---- JSON-LD helper ----------------------------------------------------------

/// <summary>
/// Decode percent-encoded characters in @id IRIs within JSON-LD output.
/// dotNetRDF encodes non-ASCII characters (e.g. Chinese) as %XX, making
/// the output unreadable. This post-processing restores readable IRIs.
/// </summary>
static string DecodeIrisInJsonLd(string jsonld)
{
    try
    {
        var jArr = JArray.Parse(jsonld);
        DecodeIrisRecursive(jArr);
        return jArr.ToString(Newtonsoft.Json.Formatting.Indented);
    }
    catch
    {
        return jsonld;
    }
}

static void DecodeIrisRecursive(JToken token)
{
    if (token is JObject obj)
    {
        if (obj.TryGetValue("@id", out var id) && id.Type == JTokenType.String)
            obj["@id"] = Uri.UnescapeDataString(id.Value<string>()!);
        foreach (var prop in obj.Properties())
            DecodeIrisRecursive(prop.Value);
    }
    else if (token is JArray arr)
    {
        foreach (var item in arr)
            DecodeIrisRecursive(item);
    }
}

app.Run();

public partial class Program { }
