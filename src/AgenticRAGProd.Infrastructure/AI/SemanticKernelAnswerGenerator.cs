using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.AI;

internal sealed class SemanticKernelAnswerGenerator(
    Kernel kernel,
    IOptions<AzureOpenAiOptions> options)
    : IAnswerGenerator
{
    private readonly AzureOpenAiOptions _options = options.Value;

    public async Task<string> GenerateAsync(
        string instructions,
        string input,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        // History belongs to this operation, never shared across requests.
        var history = new ChatHistory();
        history.AddSystemMessage(instructions);
        history.AddUserMessage(input);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = _options.MaxOutputTokens
        };

        var chatService =
            kernel.GetRequiredService<IChatCompletionService>();

        var response = await chatService.GetChatMessageContentAsync(
            history,
            executionSettings: settings,
            kernel: kernel,
            cancellationToken: timeout.Token);

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                "The answer generation service returned no text.");
        }

        return response.Content;
    }
}