using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Infrastructure.Persistence;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class SqlitePersistenceTests
{
    private static ChatMessage Message(LocalSqlite db, Guid conversation, string id = "message-01")
        => new(id, conversation, "test42@gmail.com", "Original 'question'; DROP TABLE chat_messages; --",
            "Supported [1].", db.Clock.GetUtcNow());

    [Fact]
    public async Task RelativeDatabasePathResolvesAgainstTheSuppliedContentRoot()
    {
        using var db = new LocalSqlite();
        var store = new SqliteConversationStore(Options.Create(new SqliteOptions { DatabasePath = "chat.db" }),
            Path.GetDirectoryName(db.DatabasePath));
        await store.GetRecentMessagesAsync(Guid.NewGuid(), 1, default);
        Assert.True(File.Exists(db.DatabasePath));
    }

    [Fact]
    public async Task CreatesDatabaseAndRestoresCompletedFieldsWithANewStore()
    {
        using var db = new LocalSqlite();
        var message = Message(db, Guid.NewGuid());
        await db.RealStore().SaveAsync(message, default);
        Assert.True(File.Exists(db.DatabasePath));
        var restored = await db.RealStore().GetRecentMessagesAsync(message.ConversationId, 20, default);
        Assert.Equal(message, Assert.Single(restored.Messages));
        Assert.Equal(new[] { "answer", "conversationId", "createdAtUtc", "email", "id", "query" },
            Assert.Single(db.Messages).EnumerateObject().Select(p => p.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task DuplicateDoesNotReplaceSavedMessageButIdsCanRepeatAcrossConversations()
    {
        using var db = new LocalSqlite();
        var message = Message(db, Guid.NewGuid());
        await db.RealStore().SaveAsync(message, default);
        var error = await Assert.ThrowsAsync<ChatStorageException>(() => db.RealStore().SaveAsync(message with { Answer = "Replacement" }, default));
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        await db.RealStore().SaveAsync(message with { ConversationId = Guid.NewGuid() }, default);
        Assert.Equal(message, Assert.Single((await db.RealStore().GetRecentMessagesAsync(message.ConversationId, 20, default)).Messages));
        Assert.Equal(2, db.Messages.Count);
    }

    [Fact]
    public async Task HistoryIsBoundedIsolatedAndOrderedWithTimestampTies()
    {
        using var db = new LocalSqlite();
        var conversation = Guid.NewGuid();
        for (var i = 7; i >= 1; i--)
            await db.RealStore().SaveAsync(Message(db, conversation, $"message-{i:00}"), default);
        await db.RealStore().SaveAsync(Message(db, Guid.NewGuid()), default);
        var recent = await db.RealStore().GetRecentMessagesAsync(conversation, 3, default);
        Assert.Equal(new[] { "message-05", "message-06", "message-07" }, recent.Messages.Select(m => m.Id));
        Assert.True(recent.HasEarlierMessages);
        var all = await db.RealStore().GetRecentMessagesAsync(conversation, 7, default);
        Assert.Equal(7, all.Messages.Count);
        Assert.False(all.HasEarlierMessages);
        var missing = await db.RealStore().GetRecentMessagesAsync(Guid.NewGuid(), 7, default);
        Assert.Empty(missing.Messages);
        Assert.False(missing.HasEarlierMessages);
    }

    [Fact]
    public async Task NormalizesUtcBeforeOrdering()
    {
        using var db = new LocalSqlite();
        var message = Message(db, Guid.NewGuid()) with { CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.FromHours(5.5)) };
        await db.RealStore().SaveAsync(message, default);
        var restored = Assert.Single((await db.RealStore().GetRecentMessagesAsync(message.ConversationId, 20, default)).Messages);
        Assert.Equal(message.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, restored.CreatedAtUtc.Offset);
    }

    [Fact]
    public async Task ConcurrentStoresSaveDistinctMessages()
    {
        using var db = new LocalSqlite();
        var conversation = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Task.Run(() =>
            db.RealStore().SaveAsync(Message(db, conversation, $"message-{i:00}"), default))));
        Assert.Equal(12, (await db.RealStore().GetRecentMessagesAsync(conversation, 20, default)).Messages.Count);
    }

    [Fact]
    public async Task CancellationAndInvalidInputDoNotCreateDatabase()
    {
        using var db = new LocalSqlite();
        var store = db.RealStore();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Message(db, Guid.NewGuid()), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetRecentMessagesAsync(Guid.NewGuid(), 20, cancel.Token));
        await Assert.ThrowsAsync<ChatInputException>(() => store.SaveAsync(Message(db, Guid.Empty), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Message(db, Guid.NewGuid()) with { Answer = " " }, default));
        foreach (var limit in new[] { 0, 101 })
            await Assert.ThrowsAsync<ChatInputException>(() => store.GetRecentMessagesAsync(Guid.NewGuid(), limit, default));
        Assert.False(File.Exists(db.DatabasePath));
    }

    [Fact]
    public async Task UnwritablePathReturnsSafeSetupGuidance()
    {
        using var db = new LocalSqlite();
        await db.RealStore().SaveAsync(Message(db, Guid.NewGuid()), default);
        var store = new SqliteConversationStore(Options.Create(new SqliteOptions { DatabasePath = Path.Combine(db.DatabasePath, "child.db") }));
        var error = await Assert.ThrowsAsync<ChatStorageException>(() => store.GetRecentMessagesAsync(Guid.NewGuid(), 20, default));
        Assert.Contains("Sqlite:DatabasePath", error.Message);
        Assert.DoesNotContain(db.DatabasePath, error.Message);
    }
}
