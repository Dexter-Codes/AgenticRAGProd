namespace AgenticRAGProd.Api.Contracts;

public sealed record ChatRequest(Guid? ConversationId, string Query);
