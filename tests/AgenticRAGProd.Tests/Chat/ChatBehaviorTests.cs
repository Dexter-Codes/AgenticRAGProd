using System.Text.Json;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Chunking;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatBehaviorTests
{
    [Fact]
    public async Task NewConversationSavesMessageAndReturnsSourcesWithoutPersistingThem()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("The sensor is described here [1].");
        var first = await h.Service.SendAsync(null, "Question", default);
        Assert.NotEqual(Guid.Empty, first.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(first.MessageId));
        Assert.Single(h.Model.Calls);
        Assert.Single(h.Embeddings.Questions);
        Assert.Single(h.Retrieval.Questions);
        var message = Assert.Single(h.Db.Messages);
        Assert.Equal(first.MessageId, message.GetProperty("id").GetString());
        Assert.False(message.TryGetProperty("sourcesId", out _));
        Assert.Equal(first.Answer, message.GetProperty("answer").GetString());
        Assert.Equal(h.Retrieval.Chunks[0].DocumentId, first.Sources[0].DocumentId);
        Assert.Equal(3, first.Sources[0].Context!.PageEnd);
        Assert.Contains("Uncertain layout", first.Sources[0].Context!.Warnings);
    }

    [Fact]
    public async Task RepeatedQuestionCreatesFreshMessagesInAnExistingConversation()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("First answer [1].");
        var first = await h.Service.SendAsync(null, "Question", default);
        h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.Responses.Enqueue("{\"question\":\"Question\",\"clarification\":null}");
        h.Model.Responses.Enqueue("New answer [1].");
        var second = await h.Service.SendAsync(first.ConversationId, "Question", default);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal("New answer [1].", second.Answer);
        Assert.Equal(3, h.Model.Calls.Count);
        Assert.Equal(2, h.Embeddings.Questions.Count);
        Assert.Equal(2, h.Retrieval.Questions.Count);
        Assert.Equal(2, h.Db.Messages.Count);

        Assert.All(h.Db.Messages, message => Assert.False(message.TryGetProperty("sourcesId", out _)));
    }

    [Fact]
    public async Task RepeatedNullConversationSubmissionsCreateSeparateConversations()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("First answer [1].");
        h.Model.Responses.Enqueue("Second answer [1].");
        var first = await h.Service.SendAsync(null, "Question", default);
        var second = await h.Service.SendAsync(null, "Question", default);
        Assert.NotEqual(first.ConversationId, second.ConversationId);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Equal(2, h.Db.Messages.Count);
    }

    [Fact]
    public async Task NewMessagesKeepEmailAndDistinctIdsAcrossServiceRecreation()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("First answer [1].");
        var first = await h.Service.SendAsync(null, "Initial question", default);
        h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.Responses.Enqueue("{\"question\":\"Second question\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Second answer [1].");
        var second = await h.Service.SendAsync(first.ConversationId, "Follow-up", default);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.NotEqual(first.MessageId, second.MessageId);
        var history = await h.Db.Store().GetRecentMessagesAsync(first.ConversationId, 20, default);
        Assert.Equal(new[] { "Initial question", "Follow-up" }, history.Messages.Select(m => m.Query));
        Assert.Matches(@"^test[1-9][0-9]*@gmail\.com$", history.Messages[0].Email);
        Assert.Equal(history.Messages[0].Email, history.Messages[1].Email);
        Assert.Equal(2, h.Db.Messages.Count);
    }

    [Fact]
    public async Task FollowupRestoresHistoryOnlyForResolutionAndRetrievesFreshEvidence()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("OLD_ASSISTANT_ASSERTION [1].");
        var first = await h.Service.SendAsync(null, "Tell me about the sensor", default);
        h.Model.Responses.Enqueue("{\"question\":\"How is the sensor calibrated?\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Use the fresh calibration guidance [1].");
        await h.Service.SendAsync(first.ConversationId, "How is it calibrated?", default);
        Assert.Contains("OLD_ASSISTANT_ASSERTION", h.Model.Calls[1].Input);
        Assert.DoesNotContain("Fresh sensor evidence", h.Model.Calls[1].Input);
        Assert.DoesNotContain("OLD_ASSISTANT_ASSERTION", h.Model.Calls[2].Input);
        Assert.Contains("Fresh sensor evidence", h.Model.Calls[2].Input);
        Assert.Contains("untrusted", h.Model.Calls[1].Instructions);
        Assert.Contains("untrusted", h.Model.Calls[2].Instructions);
        Assert.Equal("How is the sensor calibrated?", h.Embeddings.Questions[1]);
        Assert.Equal(2, h.Retrieval.Questions.Count);
    }

    [Fact]
    public async Task ClarificationPersistsAnswerWithoutDocumentSearchOrSourceStorage()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("Initial answer [1].");
        var first = await h.Service.SendAsync(null, "Initial question", default);
        h.Model.Responses.Enqueue("{\"question\":null,\"clarification\":\"Which sensor do you mean?\"}");
        var clarification = await h.Service.SendAsync(first.ConversationId, "What about the other one?", default);
        Assert.Equal("Which sensor do you mean?", clarification.Answer);
        Assert.Empty(clarification.Sources);

        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Single(h.Retrieval.Questions);
    }

    [Theory]
    [InlineData("No citations at all.")]
    [InlineData("Unknown source [9].")]
    [InlineData("Grouped references [1, 900] and [1].")]
    [InlineData("Negative reference [-1] and [1].")]
    [InlineData("")]
    public async Task InvalidAnswersWriteNothingAndAllowAnotherSubmission(string invalid)
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue(invalid);
        await Assert.ThrowsAsync<ChatGenerationException>(() => h.Service.SendAsync(null, "Question", default));
        Assert.Empty(h.Db.Messages);

        h.Model.Responses.Enqueue("Valid answer [1].");
        Assert.Equal("Valid answer [1].", (await h.Service.SendAsync(null, "Question", default)).Answer);
        Assert.Single(h.Db.Messages);
    }

    [Fact]
    public async Task InvalidResolutionCreatesNoFailedRecord()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("Initial answer [1].");
        var first = await h.Service.SendAsync(null, "Initial question", default);
        h.Model.Responses.Enqueue("not JSON");
        await Assert.ThrowsAsync<ChatGenerationException>(() => h.Service.SendAsync(first.ConversationId, "Follow-up", default));
        Assert.Single(h.Retrieval.Questions);
        Assert.Single(h.Db.Messages);
    }

    [Fact]
    public async Task EmptyRetrievalReturnsInsufficientWithoutAnswerGeneration()
    {
        var h = new ChatHarness();
        h.Retrieval.Chunks = [];
        var result = await h.Service.SendAsync(null, "Question", default);
        Assert.Equal("The supplied document excerpts do not provide enough information.", result.Answer);
        Assert.Empty(result.Sources);
        Assert.Single(h.Retrieval.Questions);
        Assert.Empty(h.Model.Calls);
    }

    [Fact]
    public async Task ResubmittingAfterLostResponseCreatesAnotherConversation()
    {
        var h = new ChatHarness();
        h.Db.LoseNextMessageResponse = true;
        h.Model.Responses.Enqueue("Saved response [1].");
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.SendAsync(null, "Question", default));
        var saved = Assert.Single(h.Db.Messages);
        h.Model.Responses.Enqueue("Another response [1].");
        var result = await h.Service.SendAsync(null, "Question", default);
        Assert.Equal("Another response [1].", result.Answer);
        Assert.NotEqual(saved.GetProperty("conversationId").GetString(), result.ConversationId.ToString("N"));
        Assert.NotEqual(saved.GetProperty("id").GetString(), result.MessageId);
        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Equal(2, h.Db.Messages.Count);
    }

    [Fact]
    public async Task MessageWriteFailureLeavesNoSourcesAndAllowsAnotherSubmission()
    {
        var h = new ChatHarness();
        h.Db.FailNextMessageCreate = true;
        h.Model.Responses.Enqueue("Unpersisted [1].");
        await Assert.ThrowsAsync<Microsoft.Azure.Cosmos.CosmosException>(() => h.Service.SendAsync(null, "Question", default));
        Assert.Empty(h.Db.Messages);

        h.Model.Responses.Enqueue("Recovered [1].");
        var result = await h.Service.SendAsync(null, "Question", default);
        Assert.Equal("Recovered [1].", result.Answer);
        Assert.False(Assert.Single(h.Db.Messages).TryGetProperty("sourcesId", out _));
    }

    [Fact]
    public async Task CancellationDuringGenerationLeavesNoProcessingRecords()
    {
        var h = new ChatHarness();
        using var cts = new CancellationTokenSource();
        h.Model.Handler = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Service.SendAsync(null, "Question", cts.Token));
        Assert.Empty(h.Db.Messages);
    }

    [Fact]
    public void HistoryBudgetCountsSerializedPairsAndKeepsNewest()
    {
        var h = new ChatHarness { Options = new ChatOptions { HistoryTokens = 128, HistoryTurns = 3 } };
        var id = Guid.NewGuid();
        var turns = Enumerable.Range(1, 8).Select(i => new ChatMessage($"m{i}", id, "test1@gmail.com",
            $"user {i}", $"assistant {i}", DateTimeOffset.UnixEpoch.AddSeconds(i))).Reverse().ToArray();
        var history = h.Answers.BoundHistory(turns);
        Assert.Equal(new[] { "user 6", "user 7", "user 8" }, history.Select(pair => pair.User));
        Assert.True(new EmbeddingTokenCounter().Count(JsonSerializer.Serialize(history)) <= 128);
        var oversized = turns[0] with { Answer = string.Concat(Enumerable.Repeat("oversized ", 500)) };
        Assert.Empty(h.Answers.BoundHistory([oversized]));
    }

    [Fact]
    public async Task ReturnedSourcesAreExactlyTheBoundedModelContextWithContiguousNumbers()
    {
        var h = new ChatHarness { Options = new ChatOptions { EnableQueryOptimization = false, EvidenceTokens = 512 } };
        var docId = Guid.NewGuid();
        h.Retrieval.Chunks =
        [new("too-big", docId, "Guide", 1, string.Concat(Enumerable.Repeat("oversized ", 1000)), false, 1),
            new("fits", docId, "Guide", 7, "Exact selected passage.", true, .8)];
        h.Model.Responses.Enqueue("Use the selected passage [1].");
        var result = await h.Service.SendAsync(null, "Question", default);
        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.Number);
        Assert.Equal("fits", source.ChunkId);
        Assert.True(source.HasUnprocessedImages);
        using var input = JsonDocument.Parse(h.Model.Calls.Single().Input);
        var context = input.RootElement.GetProperty("sources");
        Assert.Equal(JsonSerializer.Serialize(result.Sources), context.GetRawText());

        Assert.DoesNotContain("too-big", h.Model.Calls.Single().Input);
    }

    [Fact]
    public async Task ConcurrentSubmissionsCreateSeparateExchanges()
    {
        var h = new ChatHarness();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        h.Model.Handler = token =>
        {
            if (Interlocked.Increment(ref callCount) == 2) bothEntered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var first = h.Service.SendAsync(null, "Question", default);
        var second = h.Service.SendAsync(null, "Question", default);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult("Answer [1].");
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(results[0].ConversationId, results[1].ConversationId);
        Assert.NotEqual(results[0].MessageId, results[1].MessageId);
        Assert.Equal(2, h.Db.Messages.Count);

        Assert.Equal(2, h.Model.Calls.Count);
        Assert.All(results, result => Assert.Single(result.Sources));
    }

    [Fact]
    public async Task LostResponseExchangeCanBeFoundInHistoryAfterServiceRecreation()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("Initial answer [1].");
        var first = await h.Service.SendAsync(null, "Initial question", default);
        h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.Responses.Enqueue("{\"question\":\"Follow-up question\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Saved follow-up answer [1].");
        h.Db.LoseNextMessageResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Service.SendAsync(first.ConversationId, "Follow-up question", default));
        var history = await h.Db.Store().GetRecentMessagesAsync(first.ConversationId, 20, default);
        Assert.Equal(2, history.Messages.Count);
        var saved = history.Messages[1];
        Assert.Equal("Saved follow-up answer [1].", saved.Answer);
        Assert.False(h.Db.Messages[1].TryGetProperty("sourcesId", out _));

        Assert.Equal(3, h.Model.Calls.Count);
    }

    [Fact]
    public async Task HistoryTruncationByRecordCountIsReportedToTheResolver()
    {
        var h = new ChatHarness { Options = new ChatOptions { EnableQueryOptimization = false, HistoryTurns = 1 } };
        h.Model.Responses.Enqueue("First answer [1].");
        var first = await h.Service.SendAsync(null, "First question", default);
        h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.Responses.Enqueue("{\"question\":\"Second question\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Second answer [1].");
        await h.Service.SendAsync(first.ConversationId, "Second question", default);
        h.Model.Responses.Enqueue("{\"question\":null,\"clarification\":\"Which earlier topic?\"}");
        await h.Service.SendAsync(first.ConversationId, "The earlier one?", default);
        using var input = JsonDocument.Parse(h.Model.Calls.Last().Input);
        Assert.True(input.RootElement.GetProperty("historyOmitted").GetBoolean());
        Assert.Single(input.RootElement.GetProperty("history").EnumerateArray());
        Assert.DoesNotContain("First answer", h.Model.Calls.Last().Input);
    }

    [Fact]
    public async Task UnknownSuppliedConversationDoesNotGenerateAnAnswer()
    {
        var h = new ChatHarness();
        await Assert.ThrowsAsync<ConversationNotFoundException>(() => h.Service.SendAsync(Guid.NewGuid(), "Question", default));
        Assert.Empty(h.Model.Calls);
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Db.Messages);
    }
}
