namespace AgenticRAGProd.Application.Documents;

public sealed record ExtractedWord(
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    double FontSize = 0,
    bool IsBold = false);
