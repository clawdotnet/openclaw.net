using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Environment variables";
}
