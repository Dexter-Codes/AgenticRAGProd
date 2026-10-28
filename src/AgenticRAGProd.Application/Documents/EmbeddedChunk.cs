namespace AgenticRAGProd.Application.Documents;

public sealed record EmbeddedChunk(
    DocumentChunk Chunk,
    float[] Vector);