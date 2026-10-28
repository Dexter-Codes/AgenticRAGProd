using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

public interface IDocumentChunker
{
    IReadOnlyList<DocumentChunk> Chunk(
        Guid documentId,
        string documentName,
        IReadOnlyList<ExtractedPage> pages);
}