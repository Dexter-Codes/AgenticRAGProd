using System.Net;
using Microsoft.Azure.Cosmos;
using Moq;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Persistence;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class CosmosPersistenceTests
{
    private static ChatMessage Message(LocalCosmos db, Guid conversationId,
        string id = "message-01", string answer = "Supported [1].", string query = "Original question")
        => new(id, conversationId, "test42@gmail.com", query, answer, db.Clock.GetUtcNow());

    [Fact]
    public async Task SavesOnlyTheCompletedMessageFields()
    {
        var db = new LocalCosmos();
        var id = Guid.NewGuid();
        var message = Message(db, id);
        await db.Store().SaveAsync(message, default);
        Assert.Equal(new[] { "messages:" + message.Id }, db.Writes);
        var stored = Assert.Single(db.Messages);
        Assert.Equal(new[] { "answer", "conversationId", "createdAtUtc", "email", "id", "query" },
            stored.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(id.ToString("N"), stored.GetProperty("conversationId").GetString());
        Assert.Equal(message.Query, stored.GetProperty("query").GetString());
        Assert.Equal(message.Email, stored.GetProperty("email").GetString());
        Assert.Equal(message.Answer, stored.GetProperty("answer").GetString());
        Assert.Equal(message, Assert.Single((await db.Store().GetRecentMessagesAsync(id, 20, default)).Messages));
    }

    [Fact]
    public async Task ClarificationSavesMessageWithoutRequiringSourceStorage()
    {
        var db = new LocalCosmos();
        var message = Message(db, Guid.NewGuid(), answer: "Which notification do you mean?");
        await db.Store().SaveAsync(message, default);
        Assert.Single(db.Messages);
    }

    [Fact]
    public async Task InvalidCompletedMessageIsRejectedBeforeAnyWrite()
    {
        var db = new LocalCosmos();
        var message = Message(db, Guid.NewGuid(), answer: " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store().SaveAsync(message, default));
        Assert.Empty(db.Messages);
        Assert.Empty(db.Writes);
        db.MessagesContainer.VerifyNoOtherCalls();
    }

    [Fact]
    public void StoreResolvesOnlyTheConfiguredChatContainer()
    {
        var client = new Mock<CosmosClient>(MockBehavior.Strict);
        var messages = new Mock<Container>(MockBehavior.Strict);
        client.Setup(value => value.GetContainer("rag-demo", "chat")).Returns(messages.Object);
        _ = new CosmosConversationStore(client.Object, Microsoft.Extensions.Options.Options.Create(
            new AgenticRAGProd.Infrastructure.Configuration.CosmosDbOptions()));
        client.Verify(value => value.GetContainer("rag-demo", "chat"), Times.Once);
        client.VerifyNoOtherCalls();
        messages.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingMessagesContainerReportsRequiredSetup()
    {
        var db = new LocalCosmos();
        var message = Message(db, Guid.NewGuid());
        var providerError = new CosmosException("SYNTHETIC_PRIVATE_PROVIDER_DETAIL AccountKey=secret",
            HttpStatusCode.NotFound, 0, "private-activity", 0);
        db.MessagesContainer.Setup(container => container.CreateItemAsync(
                It.IsAny<CosmosConversationStore.MessageItem>(), It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(providerError);

        var error = await Assert.ThrowsAsync<ChatStorageException>(() => db.Store().SaveAsync(message, default));

        Assert.Contains("CosmosDb:ContainerName", error.Message);
        Assert.Contains("CosmosDb:DatabaseName", error.Message);
        Assert.Contains("/conversationId", error.Message);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", error.Message);
        Assert.DoesNotContain("AccountKey", error.Message);
        Assert.Same(providerError, error.InnerException);
        Assert.Empty(db.Messages);

        Assert.Empty(db.Writes);
    }

    [Theory]
    [InlineData("The order by query does not have a corresponding composite index. AccountKey=secret")]
    [InlineData("The query requires COMPOSITE INDEXES. AccountKey=secret")]
    public async Task MissingCompositeIndexReportsRequiredHistoryIndexWithoutProviderDetails(string providerMessage)
    {
        var db = new LocalCosmos();
        var providerError = new CosmosException(providerMessage, HttpStatusCode.BadRequest, 0, "private-activity", 0);
        ConfigureHistoryFailure(db, providerError);

        var error = await Assert.ThrowsAsync<ChatStorageException>(() => db.Store().GetRecentMessagesAsync(Guid.NewGuid(), 20, default));

        Assert.Contains("CosmosDb:ContainerName", error.Message);
        Assert.Contains("/createdAtUtc DESC", error.Message);
        Assert.Contains("/id DESC", error.Message);
        Assert.Contains("existing indexing policy", error.Message);
        Assert.DoesNotContain("AccountKey", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Same(providerError, error.InnerException);
        Assert.Empty(db.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "A different invalid query error.")]
    [InlineData(HttpStatusCode.Forbidden, "Access denied; composite index metadata is unavailable.")]
    public async Task OtherHistoryProviderErrorsAreNotMisreportedAsMissingIndexes(HttpStatusCode status, string providerMessage)
    {
        var db = new LocalCosmos();
        var providerError = new CosmosException(providerMessage, status, 0, "private-activity", 0);
        ConfigureHistoryFailure(db, providerError);

        var error = await Assert.ThrowsAsync<CosmosException>(() => db.Store().GetRecentMessagesAsync(Guid.NewGuid(), 20, default));

        Assert.Same(providerError, error);
        Assert.Empty(db.Writes);
    }

    [Fact]
    public async Task MessageFailureLeavesNoStoredMessage()
    {
        var db = new LocalCosmos { FailNextMessageCreate = true };
        var message = Message(db, Guid.NewGuid());
        await Assert.ThrowsAsync<CosmosException>(() => db.Store().SaveAsync(message, default));
        Assert.Empty(db.Messages);

        Assert.Empty(db.Writes);
        Assert.Empty((await db.Store().GetRecentMessagesAsync(message.ConversationId, 20, default)).Messages);
    }

    [Fact]
    public async Task AmbiguousMessageCommitRemainsReadable()
    {
        var db = new LocalCosmos { LoseNextMessageResponse = true };
        var message = Message(db, Guid.NewGuid());
        await Assert.ThrowsAsync<HttpRequestException>(() => db.Store().SaveAsync(message, default));
        Assert.Single(db.Messages);

        var restored = Assert.Single((await db.Store().GetRecentMessagesAsync(message.ConversationId, 20, default)).Messages);
        Assert.Equal(message, restored);
    }

    [Fact]
    public async Task MessageCreateCollisionFailsWithoutReplacingExistingData()
    {
        var db = new LocalCosmos();
        var id = Guid.NewGuid();
        var colliding = Message(db, id, answer: "Unstored answer [1].");
        var existing = Message(db, id, answer: "Existing answer [1].");
        db.BeforeNextMessageCreate = async () => { await db.Store().SaveAsync(existing, default); };
        var error = await Assert.ThrowsAsync<CosmosException>(() => db.Store().SaveAsync(colliding, default));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(existing, Assert.Single((await db.Store().GetRecentMessagesAsync(id, 20, default)).Messages));
        Assert.Single(db.Messages);

        Assert.Single(db.Writes);
    }

    [Fact]
    public async Task ConcurrentDistinctMessagesNeedNoSourceOrCoordinationRecords()
    {
        var db = new LocalCosmos();
        var id = Guid.NewGuid();
        var first = Message(db, id, "message-01");
        var second = Message(db, id, "message-02");
        await Task.WhenAll(db.Store().SaveAsync(first, default), db.Store().SaveAsync(second, default));
        Assert.Equal(2, db.Messages.Count);

        Assert.All(db.Messages, message => Assert.False(message.TryGetProperty("sourcesId", out _)));
        Assert.All(db.Messages, message => Assert.False(message.TryGetProperty("status", out _)));
        Assert.All(db.Messages, message => Assert.False(message.TryGetProperty("type", out _)));
        Assert.All(db.Messages, message => Assert.False(message.TryGetProperty("attemptId", out _)));
    }

    [Fact]
    public async Task CreatedAtUtcIsNormalizedForStorageAndReadback()
    {
        var db = new LocalCosmos();
        var message = Message(db, Guid.NewGuid()) with
        {
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.FromHours(5.5))
        };
        await db.Store().SaveAsync(message, default);

        var stored = Assert.Single(db.Messages);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", stored.GetProperty("createdAtUtc").GetString());
        var restored = Assert.Single((await db.Store().GetRecentMessagesAsync(message.ConversationId, 20, default)).Messages);
        Assert.Equal(message.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, restored.CreatedAtUtc.Offset);
    }

    [Fact]
    public async Task HistoryIsPartitionedBoundedAndDeterministicForTimestampTies()
    {
        var db = new LocalCosmos();
        var id = Guid.NewGuid();
        for (var i = 7; i >= 1; i--)
            await db.Store().SaveAsync(Message(db, id, $"message-{i:00}"), default);
        await db.Store().SaveAsync(Message(db, Guid.NewGuid(), "must-not-appear"), default);

        var recent = await db.Store().GetRecentMessagesAsync(id, 3, default);

        Assert.Equal(new[] { "message-05", "message-06", "message-07" }, recent.Messages.Select(message => message.Id));
        Assert.True(recent.HasEarlierMessages);
        Assert.Equal(5, db.QueryPagesRead); // Empty SDK page plus four rows, including the look-ahead row.
        var all = await db.Store().GetRecentMessagesAsync(id, 20, default);
        Assert.Equal(7, all.Messages.Count);
        Assert.False(all.HasEarlierMessages);
        Assert.DoesNotContain(all.Messages, message => message.Id == "must-not-appear");
        Assert.All(db.Queries, query => Assert.DoesNotContain("type", query));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task HistoryRejectsUnboundedPageSizes(int limit)
    {
        var db = new LocalCosmos();
        await Assert.ThrowsAsync<ChatInputException>(() => db.Store().GetRecentMessagesAsync(Guid.NewGuid(), limit, default));
        Assert.Empty(db.Queries);
    }

    [Fact]
    public async Task RecentHistoryIsEmptyForAnotherConversation()
    {
        var db = new LocalCosmos();
        var message = Message(db, Guid.NewGuid());
        await db.Store().SaveAsync(message, default);
        var missing = await db.Store().GetRecentMessagesAsync(Guid.NewGuid(), 20, default);
        Assert.Empty(missing.Messages);
        Assert.False(missing.HasEarlierMessages);
    }

    private static void ConfigureHistoryFailure(LocalCosmos db, CosmosException error)
    {
        var iterator = new Mock<FeedIterator<CosmosConversationStore.MessageItem>>();
        iterator.SetupGet(value => value.HasMoreResults).Returns(true);
        iterator.Setup(value => value.ReadNextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(error);
        db.MessagesContainer.Setup(container => container.GetItemQueryIterator<CosmosConversationStore.MessageItem>(
                It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns(iterator.Object);
    }
}
