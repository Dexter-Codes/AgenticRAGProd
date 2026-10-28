using AgenticRAGProd.Application.Conversations;

namespace AgenticRAGProd.Application.Abstractions;

public interface IConversationStore
{
    Task<ChatHistoryPage> GetRecentMessagesAsync(Guid conversationId, int limit, CancellationToken cancellationToken);
    Task SaveAsync(ChatMessage message, CancellationToken cancellationToken);
}
