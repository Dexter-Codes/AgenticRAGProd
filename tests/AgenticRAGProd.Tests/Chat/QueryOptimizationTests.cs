using System.Text.Json;
using AgenticRAGProd.Application.Conversations;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class QueryOptimizationTests
{
    private static ChatHarness Enabled() => new() { Options = new ChatOptions() };
    private static string Plan(string? question, string? retrievalQuestion, string? clarification = null) =>
        JsonSerializer.Serialize(new { question, retrievalQuestion, clarification });

    [Fact]
    public async Task InitialQuestionUsesOptimizedTextForBothSearchPathsButKeepsOriginalIntentAndStorage()
    {
        var h = Enabled();
        const string original = "Could you please explain Policy v2.1 notification requirements within 30 days?";
        const string retrieval = "Policy v2.1 notification requirements within 30 days";
        h.Model.Responses.Enqueue(Plan("Policy v2.1 notification requirements within 30 days?", retrieval));
        h.Model.Responses.Enqueue("The fresh guidance applies [1].");

        var response = await h.Service.SendAsync(null, original, default);

        Assert.True(h.Options.EnableQueryOptimization);
        Assert.Equal(retrieval, Assert.Single(h.Embeddings.Questions));
        Assert.Equal(retrieval, Assert.Single(h.Retrieval.Questions));
        Assert.Equal(2, h.Model.Calls.Count);
        using var preparation = JsonDocument.Parse(h.Model.Calls[0].Input);
        Assert.Equal(original, preparation.RootElement.GetProperty("message").GetString());
        Assert.Empty(preparation.RootElement.GetProperty("history").EnumerateArray());
        using var answer = JsonDocument.Parse(h.Model.Calls[1].Input);
        Assert.Equal(original, answer.RootElement.GetProperty("originalQuestion").GetString());
        Assert.Equal(original, answer.RootElement.GetProperty("question").GetString());
        Assert.Equal(original, Assert.Single(h.Db.Messages).GetProperty("query").GetString());
        Assert.False(h.Db.Messages[0].TryGetProperty("retrievalQuestion", out _));
        Assert.Single(response.Sources);
    }

    [Fact]
    public async Task FollowupResolutionAndOptimizationShareOneCallAndHistoryNeverBecomesEvidence()
    {
        var h = Enabled();
        const string original = "How is it calibrated?";
        const string standalone = "How is the pressure sensor calibrated?";
        const string retrieval = "pressure sensor calibration procedure";
        var history = new ChatMessage("previous", Guid.NewGuid(), "test1@gmail.com", "Describe the pressure sensor",
            "OLD_ASSISTANT_ASSERTION [1].", DateTimeOffset.UnixEpoch);
        h.Model.Responses.Enqueue(Plan(standalone, retrieval));
        h.Model.Responses.Enqueue("Use the fresh guidance [1].");

        await h.Answers.AnswerAsync(original, [history], default);

        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Contains("OLD_ASSISTANT_ASSERTION", h.Model.Calls[0].Input);
        Assert.DoesNotContain("Fresh sensor evidence", h.Model.Calls[0].Input);
        Assert.DoesNotContain("OLD_ASSISTANT_ASSERTION", h.Model.Calls[1].Input);
        using var answer = JsonDocument.Parse(h.Model.Calls[1].Input);
        Assert.Equal(original, answer.RootElement.GetProperty("originalQuestion").GetString());
        Assert.Equal(standalone, answer.RootElement.GetProperty("question").GetString());
        Assert.Equal(retrieval, Assert.Single(h.Embeddings.Questions));
        Assert.Equal(retrieval, Assert.Single(h.Retrieval.Questions));
    }

    [Fact]
    public async Task AmbiguousInitialQueryIsClarifiedWithoutEmbeddingSearchOrAnswerGeneration()
    {
        var h = Enabled();
        h.Model.Responses.Enqueue(Plan(null, null, "Which notification policy do you mean?"));

        var result = await h.Service.SendAsync(null, "What about its deadline?", default);

        Assert.Equal("Which notification policy do you mean?", result.Answer);
        Assert.Empty(result.Sources);
        Assert.Single(h.Model.Calls);
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Retrieval.Questions);
        Assert.Single(h.Db.Messages);
    }

    [Fact]
    public async Task DisabledOptimizationRetainsDirectFirstQuestionAndExistingFollowupResolver()
    {
        var h = new ChatHarness();
        h.Model.Responses.Enqueue("First answer [1].");
        var first = await h.Service.SendAsync(null, "  Initial question  ", default);
        Assert.Single(h.Model.Calls);
        Assert.Equal("Initial question", Assert.Single(h.Embeddings.Questions));
        h.Model.Responses.Enqueue("{\"question\":\"Standalone follow-up\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Follow-up answer [1].");

        await h.Service.SendAsync(first.ConversationId, "What about it?", default);

        Assert.Equal(3, h.Model.Calls.Count);
        Assert.Equal("Standalone follow-up", h.Embeddings.Questions[1]);
        Assert.Equal("Standalone follow-up", h.Retrieval.Questions[1]);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"question\":\"Q\",\"clarification\":null}")]
    [InlineData("{\"question\":\"Q\",\"retrievalQuestion\":\"R\",\"clarification\":null,\"extra\":true}")]
    [InlineData("{\"question\":\"Q\",\"question\":\"R\",\"clarification\":null}")]
    [InlineData("{\"question\":true,\"retrievalQuestion\":\"R\",\"clarification\":null}")]
    [InlineData("{\"question\":\"Q\",\"retrievalQuestion\":[],\"clarification\":null}")]
    [InlineData("{\"question\":\"Q\",\"retrievalQuestion\":\" \",\"clarification\":null}")]
    [InlineData("{\"question\":\"Q\",\"retrievalQuestion\":\"R\",\"clarification\":\"Which one?\"}")]
    [InlineData("{\"question\":null,\"retrievalQuestion\":null,\"clarification\":\"Missing question mark\"}")]
    public async Task InvalidPreparationCannotTriggerSearchOrPersistAnExchange(string output)
    {
        var h = Enabled();
        h.Model.Responses.Enqueue(output);
        await Assert.ThrowsAsync<ChatGenerationException>(() => h.Service.SendAsync(null, "Question", default));
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Retrieval.Questions);
        Assert.Empty(h.Db.Messages);
        Assert.Single(h.Model.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedPreparedQueriesAreRejected(bool oversizedStandalone)
    {
        var h = Enabled();
        h.Model.Responses.Enqueue(Plan(oversizedStandalone ? new string('x', 2001) : "Question",
            oversizedStandalone ? "Question" : new string('x', 2001)));
        await Assert.ThrowsAsync<ChatGenerationException>(() => h.Service.SendAsync(null, "Question", default));
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Db.Messages);
    }

    [Theory]
    [InlineData("What is the 30 day deadline?", "What is the 130 day deadline?")]
    [InlineData("What is the 30 day deadline?", "What is the deadline?")]
    [InlineData("What is the ٣٠ day deadline?", "What is the ١٣٠ day deadline?")]
    [InlineData("What is the deadline?", "What is the 30 day deadline?")]
    [InlineData("Policy v2.1 effective 2024-10-27", "Policy v2.2 effective 2024-10-27")]
    [InlineData("Threshold -5% and $30.50", "Threshold 5% and $30.50")]
    [InlineData("What does \"material breach\" mean?", "What is a breach?")]
    [InlineData("What does “material breach” mean?", "What is a breach?")]
    [InlineData("What does \"cat\" mean?", "catalog meaning")]
    [InlineData("What does \"material breach\" mean?", "material breaches meaning")]
    public async Task UnsafeLiteralChangesFallBackToTheCompleteQuestionWithoutAnotherModelCall(string original, string rewrite)
    {
        var h = Enabled();
        h.Model.Responses.Enqueue(Plan(original, rewrite));
        h.Model.Responses.Enqueue("Supported answer [1].");
        await h.Service.SendAsync(null, original, default);
        Assert.Equal(original, Assert.Single(h.Embeddings.Questions));
        Assert.Equal(original, Assert.Single(h.Retrieval.Questions));
        Assert.Equal(2, h.Model.Calls.Count);
    }

    [Fact]
    public async Task SafeRewriteRetainingQuotedTextAndVersionIsUsedForRetrieval()
    {
        var h = Enabled();
        const string original = "Could you explain \"material breach\" in Policy v2.1?";
        const string retrieval = "Policy v2.1 \"material breach\" definition";
        h.Model.Responses.Enqueue(Plan(original, retrieval));
        h.Model.Responses.Enqueue("Supported answer [1].");
        await h.Service.SendAsync(null, original, default);
        Assert.Equal(retrieval, Assert.Single(h.Embeddings.Questions));
        Assert.Equal(retrieval, Assert.Single(h.Retrieval.Questions));
    }

    [Fact]
    public async Task LosingAnExplicitFollowupConstraintFailsBeforeRetrieval()
    {
        var h = Enabled();
        var previous = new ChatMessage("previous", Guid.NewGuid(), "test1@gmail.com", "Describe the policy", "Earlier response [1].", DateTimeOffset.UnixEpoch);
        h.Model.Responses.Enqueue(Plan("What is the policy deadline?", "policy deadline"));
        await Assert.ThrowsAsync<ChatGenerationException>(() => h.Answers.AnswerAsync("What about 30 days?", [previous], default));
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Retrieval.Questions);
    }

    [Fact]
    public async Task CancellationAfterPreparationStopsBeforeAnyRetrieval()
    {
        var h = Enabled();
        using var cancellation = new CancellationTokenSource();
        h.Model.Handler = _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Plan("Question", "Question"));
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Service.SendAsync(null, "Question", cancellation.Token));
        Assert.Empty(h.Embeddings.Questions);
        Assert.Empty(h.Retrieval.Questions);
        Assert.Empty(h.Db.Messages);
    }

    [Fact]
    public async Task PreparationProviderFailureStaysAnErrorAndEmptyRetrievalStaysInsufficient()
    {
        var h = Enabled();
        h.Model.Handler = _ => throw new IOException("Synthetic provider failure");
        await Assert.ThrowsAsync<IOException>(() => h.Service.SendAsync(null, "Question", default));
        Assert.Empty(h.Db.Messages);
        Assert.Empty(h.Embeddings.Questions);
        h.Model.Handler = null;
        h.Model.Responses.Enqueue(Plan("Question", "Question"));
        h.Retrieval.Chunks = [];
        var result = await h.Service.SendAsync(null, "Question", default);
        Assert.Equal("The supplied document excerpts do not provide enough information.", result.Answer);
        Assert.Empty(result.Sources);
        Assert.Equal(2, h.Model.Calls.Count);
    }
}
