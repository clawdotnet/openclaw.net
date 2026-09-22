using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResolverAccessorCollection
{
    public const string Name = "Resolver accessor";
}
