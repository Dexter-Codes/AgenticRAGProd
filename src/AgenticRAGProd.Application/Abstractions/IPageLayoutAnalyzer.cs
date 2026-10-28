using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

public interface IPageLayoutAnalyzer
{
    IReadOnlyList<LayoutRow> Analyze(
        IReadOnlyList<ExtractedWord> words);
}