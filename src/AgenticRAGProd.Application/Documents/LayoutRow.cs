namespace AgenticRAGProd.Application.Documents;

public sealed record LayoutCell(
    string Text,
    double Left,
    double Right);

public sealed record LayoutRow(
    double Bottom,
    IReadOnlyList<LayoutCell> Cells);