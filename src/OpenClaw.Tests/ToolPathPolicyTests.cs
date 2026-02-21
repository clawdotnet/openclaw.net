using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolPathPolicyTests
{
    [Fact]
    public void IsReadAllowed_DeniesSymlinkEscapingAllowedRoot()
    {
        // Only run on Unix where symlinks are reliable without elevation
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempDir();
        var outsideDir = CreateTempDir();
        var secretFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(secretFile, "sensitive");

        // Create a symlink inside the allowed root that points outside
        var symlinkPath = Path.Combine(root, "escape.txt");
        File.CreateSymbolicLink(symlinkPath, secretFile);

        var config = new ToolingConfig
        {
            AllowedReadRoots = [root]
        };

        // The symlink resolves outside the allowed root — must be denied
        Assert.False(ToolPathPolicy.IsReadAllowed(config, symlinkPath));
    }

    [Fact]
    public void IsReadAllowed_AllowsSymlinkInsideAllowedRoot()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempDir();
        var subDir = Path.Combine(root, "sub");
        Directory.CreateDirectory(subDir);
        var realFile = Path.Combine(subDir, "real.txt");
        File.WriteAllText(realFile, "ok");

        // Symlink inside the root, pointing to another file inside the root
        var symlinkPath = Path.Combine(root, "link.txt");
        File.CreateSymbolicLink(symlinkPath, realFile);

        var config = new ToolingConfig
        {
            AllowedReadRoots = [root]
        };

        Assert.True(ToolPathPolicy.IsReadAllowed(config, symlinkPath));
    }

    [Fact]
    public void IsWriteAllowed_DeniesSymlinkedParentDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempDir();
        var outsideDir = CreateTempDir();

        // Create a directory symlink inside the root pointing outside
        var symlinkDir = Path.Combine(root, "escape_dir");
        Directory.CreateSymbolicLink(symlinkDir, outsideDir);

        // Writing to a file through the symlinked directory should be denied
        var targetPath = Path.Combine(symlinkDir, "new_file.txt");

        var config = new ToolingConfig
        {
            AllowedWriteRoots = [root]
        };

        Assert.False(ToolPathPolicy.IsWriteAllowed(config, targetPath));
    }

    [Fact]
    public void IsReadAllowed_WildcardAllowsAnyPath()
    {
        var config = new ToolingConfig
        {
            AllowedReadRoots = ["*"]
        };

        Assert.True(ToolPathPolicy.IsReadAllowed(config, "/any/path"));
    }

    [Fact]
    public void IsReadAllowed_EmptyRootsDeniesAll()
    {
        var config = new ToolingConfig
        {
            AllowedReadRoots = []
        };

        Assert.False(ToolPathPolicy.IsReadAllowed(config, "/any/path"));
    }

    [Fact]
    public void ResolveRealPath_NonExistentFile_ResolvesAncestorSymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempDir();
        var outsideDir = CreateTempDir();

        // Create a directory symlink
        var symlinkDir = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(symlinkDir, outsideDir);

        // Resolve a non-existent file under the symlinked directory
        var resolved = ToolPathPolicy.ResolveRealPath(Path.Combine(symlinkDir, "nonexistent.txt"));

        // The resolved path should point under the outside directory, not under root
        Assert.StartsWith(outsideDir, resolved, StringComparison.Ordinal);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
