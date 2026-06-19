using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DynamicProviderRegistryCollection
{
    public const string Name = "Dynamic provider registry";
}
