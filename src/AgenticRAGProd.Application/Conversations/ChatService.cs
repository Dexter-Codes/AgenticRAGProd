using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;

namespace AgenticRAGProd.Application.Conversations;

public sealed class ChatService(
    IConversationStore store, ChatAnswerService answers, IOptions<ChatOptions> options, TimeProvider time)
{
    public async Task<ChatResponse> SendAsync(Guid? conversationId, string query, CancellationToken cancellationToken)
    {
        Validate(conversationId, query);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
        var token = timeout.Token;

        var id = conversationId ?? Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString("N");
        var history = conversationId.HasValue
            ? await store.GetRecentMessagesAsync(id, options.Value.HistoryTurns, token)
            : new ChatHistoryPage([], false);
        if (conversationId.HasValue && history.Messages.Count == 0) 
        {
            throw new ConversationNotFoundException();
        }
        // As user login is not set yet, we will use a random email for now. This will be replaced with the actual user email once login is implemented.
        var email = history.Messages.FirstOrDefault()?.Email
            ?? $"test{RandomNumberGenerator.GetInt32(1, int.MaxValue)}@gmail.com";

        var answer = await answers.AnswerAsync(query, history.Messages, token, history.HasEarlierMessages);
        token.ThrowIfCancellationRequested();
        var createdAt = time.GetUtcNow();
        var message = new ChatMessage(messageId, id, email, query, answer.Answer, createdAt);
        await store.SaveAsync(message, token);
        return new ChatResponse(id, messageId, message.Answer, answer.Sources);
    }

    private static void Validate(Guid? conversationId, string query)
    {
        if (conversationId == Guid.Empty) throw new ChatInputException("Conversation ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new ChatInputException("Query must contain 1–2,000 characters.");
    }
}
