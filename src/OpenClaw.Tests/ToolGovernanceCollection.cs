using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToolGovernanceCollection
{
    public const string Name = "Tool governance";
}
