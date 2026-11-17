namespace AgenticRAGProd.Api.Contracts;

public sealed record TravelKnowledgeRequest(
    int DestinationId,
    string Query);