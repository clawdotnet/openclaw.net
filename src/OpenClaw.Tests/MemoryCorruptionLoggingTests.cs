using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MemoryCorruptionLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSessionAsync_WhenPersistedSessionIsCorrupt_ShouldKeepAccountIdentityOutOfLogs(bool sqlite)
    {
        var root = Path.Join(Path.GetTempPath(), "memory-corruption-logging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string sessionId = "integration:account:private-account-id";
        var logger = new CapturingLogger();
        try
        {
            MemoryStoreCorruptionException error;
            if (sqlite)
            {
                var dbPath = Path.Join(root, "memory.db");
                using var store = new SqliteMemoryStore(dbPath, enableFts: false, logger: logger);
                await store.SaveSessionAsync(new Session { Id = sessionId, ChannelId = "integration", SenderId = "account:private-account-id" }, TestContext.Current.CancellationToken);
                await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
                {
                    await connection.OpenAsync(TestContext.Current.CancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE sessions SET json = '{invalid' WHERE id = $id";
                    command.Parameters.AddWithValue("$id", sessionId);
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                error = await Assert.ThrowsAsync<MemoryStoreCorruptionException>(async () =>
                    await store.GetSessionAsync(sessionId, TestContext.Current.CancellationToken));
            }
            else
            {
                var store = new FileMemoryStore(root, logger: logger);
                await store.SaveSessionAsync(new Session { Id = sessionId, ChannelId = "integration", SenderId = "account:private-account-id" }, TestContext.Current.CancellationToken);
                var sessionFile = Assert.Single(Directory.GetFiles(Path.Join(root, "sessions"), "*.json"));
                await File.WriteAllTextAsync(sessionFile, "{invalid", TestContext.Current.CancellationToken);
                // Use a fresh store to avoid the session cache.
                store = new FileMemoryStore(root, logger: logger);
                error = await Assert.ThrowsAsync<MemoryStoreCorruptionException>(async () =>
                    await store.GetSessionAsync(sessionId, TestContext.Current.CancellationToken));
                Assert.Contains(".corrupt-", error.FilePath, StringComparison.Ordinal);
            }

            Assert.Equal(sessionId, error.SessionId);
            Assert.NotEmpty(logger.Messages);
            Assert.All(logger.Messages, message =>
            {
                Assert.DoesNotContain("private-account-id", message, StringComparison.Ordinal);
                Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(sessionId)), message, StringComparison.Ordinal);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CapturingLogger : ILogger<FileMemoryStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception);
    }
}
