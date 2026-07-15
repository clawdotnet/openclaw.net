using System.Text.Json;

namespace OpenClaw.Core.Models;

public sealed class GraphSliceProfile
{
    public Dictionary<string, SliceProfile> Profiles { get; set; } = [];
}

public sealed class SliceProfile
{
    public List<SliceSourceConfig> Sources { get; set; } = [];
    public string Construct { get; set; } = "";
    public JsonElement? Frame { get; set; }
    public SliceOutputConfig Output { get; set; } = new();
}

public sealed class SliceSourceConfig
{
    public string Kind { get; set; } = "remote-endpoint";

    // remote-endpoint fields
    public string? Endpoint { get; set; }
    public SliceAuthConfig? Auth { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public string? DefaultGraphUri { get; set; }

    // local-files fields
    public List<string>? Paths { get; set; }
    public string? NamedGraphUri { get; set; }
}

public sealed class SliceAuthConfig
{
    public string Type { get; set; } = "none";
    public string? UsernameEnv { get; set; }
    public string? PasswordEnv { get; set; }
}

public sealed class SliceOutputConfig
{
    public string Path { get; set; } = "./tmp/graph-slice.jsonld";
    public int MaxTriples { get; set; } = 50000;
    public bool Compaction { get; set; } = true;
}
