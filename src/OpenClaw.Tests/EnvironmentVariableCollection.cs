using Xunit;

namespace OpenClaw.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection : ICollectionFixture<object>
{
    public const string Name = "Environment variables";
}
