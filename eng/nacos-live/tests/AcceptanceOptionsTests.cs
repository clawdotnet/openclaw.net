using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class AcceptanceOptionsTests
{
    [Fact]
    public void Parse_ReturnsFullPathsForExistingInputsAndNewOutput()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var managedPath = temporaryDirectory.CreateFile("managed.dll");
        var nativePath = temporaryDirectory.CreateFile("native");
        var testDllPath = temporaryDirectory.CreateFile("tests.dll");
        var outputPath = Path.Combine(temporaryDirectory.Path, "evidence");

        var options = AcceptanceOptions.Parse(
        [
            "--managed", managedPath,
            "--native", nativePath,
            "--test-dll", testDllPath,
            "--output", outputPath,
        ]);

        Assert.Equal(Path.GetFullPath(managedPath), options.ManagedPath);
        Assert.Equal(Path.GetFullPath(nativePath), options.NativePath);
        Assert.Equal(Path.GetFullPath(testDllPath), options.TestDllPath);
        Assert.Equal(Path.GetFullPath(outputPath), options.OutputDirectory);
    }

    [Theory]
    [InlineData("--managed")]
    [InlineData("--native")]
    [InlineData("--test-dll")]
    [InlineData("--output")]
    public void Parse_RejectsMissingOption(string missingOption)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var arguments = new List<string>
        {
            "--managed", temporaryDirectory.CreateFile("managed.dll"),
            "--native", temporaryDirectory.CreateFile("native"),
            "--test-dll", temporaryDirectory.CreateFile("tests.dll"),
            "--output", Path.Combine(temporaryDirectory.Path, "evidence"),
        };
        arguments.RemoveRange(arguments.IndexOf(missingOption), 2);

        var exception = Assert.Throws<ArgumentException>(() => AcceptanceOptions.Parse(arguments.ToArray()));

        Assert.Contains(missingOption, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnOutputDirectoryThatAlreadyExists()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "evidence")).FullName;

        var exception = Assert.Throws<ArgumentException>(() => AcceptanceOptions.Parse(
        [
            "--managed", temporaryDirectory.CreateFile("managed.dll"),
            "--native", temporaryDirectory.CreateFile("native"),
            "--test-dll", temporaryDirectory.CreateFile("tests.dll"),
            "--output", outputPath,
        ]));

        Assert.Contains("--output", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnInputFileThatDoesNotExist()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var exception = Assert.Throws<ArgumentException>(() => AcceptanceOptions.Parse(
        [
            "--managed", Path.Combine(temporaryDirectory.Path, "missing.dll"),
            "--native", temporaryDirectory.CreateFile("native"),
            "--test-dll", temporaryDirectory.CreateFile("tests.dll"),
            "--output", Path.Combine(temporaryDirectory.Path, "evidence"),
        ]));

        Assert.Contains("--managed", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("nacos-options-test-").FullName;

        public string CreateFile(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}