using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

public interface IDocumentParser
{
    IReadOnlyList<ExtractedPage> Parse(
        Stream content,
        CancellationToken cancellationToken);
}