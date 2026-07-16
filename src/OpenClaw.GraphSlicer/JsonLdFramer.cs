using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VDS.RDF.JsonLd;
using VDS.RDF.JsonLd.Syntax;

namespace OpenClaw.GraphSlicer;

/// <summary>
/// JSON-LD 1.1 Framing processor backed by dotNetRDF's W3C-compliant
/// <see cref="JsonLdProcessor.Frame(JToken, JToken, JsonLdProcessorOptions)"/> implementation.
///
/// Supported frame keywords (read from the frame document itself, or overridden via options):
///   @context     — output context (frame wins, otherwise inherited from input)
///   @type        — filter matched subjects by rdf:type (single string or array)
///   @id          — match a specific subject by @id
///   @embed       — @always / @once / @never / @first / @last / @link
///   @explicit    — when true, only include properties listed in the frame
///   @requireAll  — when true, a node must have ALL listed properties to be matched
///   @omitDefault — when true, omit @default for missing properties
/// </summary>
public sealed class JsonLdFramer
{
    /// <summary>
    /// Apply JSON-LD 1.1 framing to a serialized JSON-LD string.
    /// Accepts both compact form (single node), expanded form (with @graph),
    /// and bare-array outputs from RDF serializers.
    /// </summary>
    /// <param name="jsonLd">The flat JSON-LD input (typically from dotNetRDF JsonLdWriter).</param>
    /// <param name="frameJson">The frame document as a JSON string.</param>
    /// <param name="options">Optional framing overrides.</param>
    /// <returns>Framed JSON-LD string.</returns>
    public string Frame(string jsonLd, string frameJson, JsonLdFrameOptions? options = null)
    {
        options ??= JsonLdFrameOptions.Default;

        var inputToken = ParseJsonLdInput(jsonLd);
        var frameToken = JToken.Parse(frameJson);

        var processorOptions = ToProcessorOptions(options);
        var result = JsonLdProcessor.Frame(inputToken, frameToken, processorOptions);

        return result.ToString(options.Indented ? Formatting.Indented : Formatting.None);
    }

    /// <summary>
    /// Parse JSON-LD input string, normalizing bare-array output from some
    /// RDF serializers into an object form dotNetRDF can process.
    /// </summary>
    private static JToken ParseJsonLdInput(string jsonLd)
    {
        var token = JToken.Parse(jsonLd);

        // Some serializers (e.g. older dotNetRDF JsonLdWriter) may output
        // a bare JSON array. Wrap it if necessary.
        if (token is JArray array)
            return new JObject { ["@graph"] = array };

        return token;
    }

    /// <summary>
    /// Map our simple options bag to dotNetRDF's comprehensive
    /// <see cref="JsonLdProcessorOptions"/>.
    /// </summary>
    private static JsonLdProcessorOptions ToProcessorOptions(JsonLdFrameOptions options) =>
        new()
        {
            CompactArrays = true,
            OmitGraph = options.OmitGraph,
            ProcessingMode = JsonLdProcessingMode.JsonLd11,
        };
}

/// <summary>
/// Options controlling JSON-LD framing behavior.
/// These are OVERRIDES — the frame document's own directives
/// (@embed, @explicit, @requireAll, @omitDefault) take precedence.
/// </summary>
public sealed class JsonLdFrameOptions
{
    /// <summary>Pretty-print the output JSON.</summary>
    public bool Indented { get; set; } = false;

    /// <summary>When true, the output omits the top-level @graph wrapper
    /// when the result contains exactly one node.</summary>
    public bool OmitGraph { get; set; } = false;

    public static readonly JsonLdFrameOptions Default = new();
}

/// <summary>
/// Controls how referenced objects are embedded during framing.
/// Maps directly to dotNetRDF's <see cref="JsonLdEmbed"/>.
/// Kept for backward compatibility; <see cref="JsonLdFramer"/> now delegates
/// to dotNetRDF which reads @embed from the frame document itself.
/// </summary>
public enum EmbedMode
{
    Default = 0,
    Always = 1,
    Once = 2,
    Never = 3,
    First = 4,
    Last = 5,
    Link = 6
}