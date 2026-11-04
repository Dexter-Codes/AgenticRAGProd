using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Application.Prompts;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatAnswerKindTests
{
    [Theory]
    [InlineData(
        "Example answer [1].",
        ChatAnswerKind.Answered)]
    [InlineData(
        PromptConstants.InsufficientEvidenceAnswer,
        ChatAnswerKind.InsufficientEvidence)]
    public async Task GeneratedAnswerHasExplicitKind(
        string text,
        ChatAnswerKind expected)
    {
        using var h = new ChatHarness
        {
            Options = new ChatOptions
            {
                EnableQueryOptimization = false
            }
        };

        h.Model.Responses.Enqueue(text);

        var result = await h.Answers.AnswerAsync(
            "Question",
            [],
            default);

        Assert.Equal(expected, result.Kind);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task EmptyRetrievalReturnsInsufficientEvidence()
    {
        using var h = new ChatHarness
        {
            Options = new ChatOptions
            {
                EnableQueryOptimization = false
            }
        };

        h.Retrieval.Chunks = [];

        var result = await h.Answers.AnswerAsync(
            "Question",
            [],
            default);

        Assert.Equal(
            ChatAnswerKind.InsufficientEvidence,
            result.Kind);

        Assert.Empty(result.Sources);
        Assert.Empty(h.Model.Calls);
    }
}