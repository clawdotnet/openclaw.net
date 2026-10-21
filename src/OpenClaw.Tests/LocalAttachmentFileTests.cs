using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// <see cref="LocalAttachmentFile"/> is the only place in the Gateway that opens an attachment file, and it
/// used to read whatever size it was pointed at. The ceiling matters because this read happens *before*
/// every budget in the system: the inliner's per-request cap and the cache's per-asset cap are both checked
/// on bytes that are already in memory.
/// </summary>
public sealed class LocalAttachmentFileTests
{
    [Fact]
    public async Task TryRead_refuses_a_file_over_the_ceiling()
    {
        var path = await WriteTempFileAsync(new byte[16]);
        try
        {
            Assert.False(LocalAttachmentFile.TryRead(path, maxBytes: 8, out var bytes));
            Assert.Empty(bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryRead_still_reads_a_file_inside_the_ceiling()
    {
        var path = await WriteTempFileAsync(new byte[16]);
        try
        {
            Assert.True(LocalAttachmentFile.TryRead(path, maxBytes: 16, out var bytes));
            Assert.Equal(16, bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_default_ceiling_is_far_above_anything_this_product_carries()
    {
        // Guards the guard: the limit exists to stop an unbounded read, not to become a limit nobody
        // expected. If someone lowers it to a plausible attachment size, inlining starts failing silently.
        var path = await WriteTempFileAsync(new byte[32]);
        try
        {
            Assert.True(LocalAttachmentFile.TryRead(path, out var bytes));
            Assert.Equal(32, bytes.Length);
            Assert.True(LocalAttachmentFile.MaxReadBytes >= 100L * 1024 * 1024);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-attachment-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}
