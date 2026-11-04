using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;

namespace AgenticRAGProd.Application.Conversations;

public sealed class ChatService(
    IConversationStore store,
    ChatAnswerService answers,
    IOptions<ChatOptions> options,
    TimeProvider time,
    ChatTelemetry telemetry)
{
    public async Task<ChatResponse> SendAsync(
        Guid? conversationId,
        string query,
        CancellationToken cancellationToken)
    {
        Validate(conversationId, query);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(
                options.Value.RequestTimeoutSeconds));

        var token = timeout.Token;
        var id = conversationId ?? Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString("N");

        using var scope =
            telemetry.BeginConversation(id, messageId);

        return await telemetry.MeasureAsync(
            "chat.total",
            async activity =>
            {
                var history = conversationId.HasValue
                    ? await telemetry.MeasureAsync(
                        "chat.history",
                        () => store.GetRecentMessagesAsync(
                            id,
                            options.Value.HistoryTurns,
                            token))
                    : new ChatHistoryPage([], false);

                activity?.SetTag("rag.history.loaded_turns", history.Messages.Count);

                if (conversationId.HasValue &&
                    history.Messages.Count == 0)
                {
                    throw new ConversationNotFoundException();
                }

                // Demonstration data until authentication exists.
                var email = history.Messages.FirstOrDefault()?.Email
                    ?? $"test{RandomNumberGenerator.GetInt32(
                        1, int.MaxValue)}@gmail.com";

                var answer = await answers.AnswerAsync(
                    query,
                    history.Messages,
                    token,
                    history.HasEarlierMessages);

                token.ThrowIfCancellationRequested();

                var message = new ChatMessage(
                    messageId,
                    id,
                    email,
                    query,
                    answer.Answer,
                    time.GetUtcNow());

                await telemetry.MeasureAsync("chat.persistence",
                    () => store.SaveAsync(message, token));

                telemetry.RecordAnswerOutcome(activity, answer);

                return new ChatResponse(
                    id,
                    messageId,
                    message.Answer,
                    answer.Sources,
                    ChatTelemetry.AnswerKindTag(answer.Kind));
            });
    }

    private static void Validate(
        Guid? conversationId,
        string query)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ChatInputException(
                "Conversation ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(query) ||
            query.Length > 2000)
        {
            throw new ChatInputException(
                "Query must contain 1–2,000 characters.");
        }
    }
}