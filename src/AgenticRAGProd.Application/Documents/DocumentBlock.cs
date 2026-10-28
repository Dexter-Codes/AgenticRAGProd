namespace AgenticRAGProd.Application.Documents;

public enum DocumentBlockKind { Prose, Heading, Table }

// A table is supplied only when a header and row boundaries are supported by
// extraction evidence. Otherwise the original text remains prose, with warnings.
public sealed record DocumentBlock(
    DocumentBlockKind Kind,
    string Text,
    int HeadingLevel = 0,
    string? TableHeader = null,
    IReadOnlyList<string>? TableRows = null);
