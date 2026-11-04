namespace AgenticRAGProd.Application.Conversations;

public sealed record ChatMessage(string Id, Guid ConversationId, string Email, string Query,
    string Answer, DateTimeOffset CreatedAtUtc);

public sealed record ChatHistoryPage(IReadOnlyList<ChatMessage> Messages, bool HasEarlierMessages);

public sealed record ChatResponse(Guid ConversationId, string MessageId, string Answer,
    IReadOnlyList<AnswerSource> Sources, string AnswerKind);

public enum ChatAnswerKind
{
    Answered,
    Clarification,
    InsufficientEvidence
}

public sealed record ChatAnswer(string Answer, IReadOnlyList<AnswerSource> Sources, ChatAnswerKind Kind);
public sealed class ConversationNotFoundException() : Exception("Conversation not found.");
public sealed class ChatInputException(string message) : Exception(message);
public sealed class ChatGenerationException(string message) : Exception(message);
public sealed class ChatStorageException(string message, Exception innerException) : Exception(message, innerException);
