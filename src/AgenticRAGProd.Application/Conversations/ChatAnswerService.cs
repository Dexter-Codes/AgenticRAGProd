using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Prompts;

namespace AgenticRAGProd.Application.Conversations;

public sealed partial class ChatAnswerService(
    IAnswerGenerator generator, ITextEmbeddingService embeddings, ICorpusChunkRetriever retriever,
    ITextTokenCounter tokens, IOptions<ChatOptions> options)
{
    public async Task<ChatAnswer> AnswerAsync(string message, IReadOnlyList<ChatMessage> chatHistory,
        CancellationToken cancellationToken, bool earlierHistoryOmitted = false)
    {
        var completed = chatHistory.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.Id, StringComparer.Ordinal).ToArray();
        var question = message.Trim();
        var retrievalQuestion = question;
        var optimize = options.Value.EnableQueryOptimization;
        if (optimize || completed.Length > 0)
        {
            var history = BoundHistory(completed);
            var resolution = await generator.GenerateAsync(optimize
                    ? PromptConstants.QueryOptimizationInstructions : PromptConstants.QueryResolutionInstructions,
                JsonSerializer.Serialize(new { message, history, historyIsBounded = true,
                    historyOmitted = earlierHistoryOmitted || history.Count < completed.Length }), cancellationToken);
            var resolved = ReadResolution(resolution, optimize);
            if (resolved.Clarification is not null) return new ChatAnswer(resolved.Clarification, []);
            // The original first question remains the answer target. Later
            // questions may need a standalone form to resolve their references.
            question = completed.Length == 0 ? message.Trim() : resolved.Question!;
            if (!PreservesLiterals(message, question, allowAdditional: true))
                throw new ChatGenerationException("Query preparation changed an explicit number or quoted phrase.");
            retrievalQuestion = optimize && PreservesLiterals(question, resolved.RetrievalQuestion!, allowAdditional: false)
                ? resolved.RetrievalQuestion! : question;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var vector = await embeddings.GenerateAsync(retrievalQuestion, cancellationToken);
        var chunks = await retriever.RetrieveCorpusAsync(retrievalQuestion, vector, cancellationToken);
        var sources = new List<AnswerSource>();
        foreach (var chunk in chunks)
        {
            var source = new AnswerSource(sources.Count + 1, chunk.Id, chunk.DocumentName, chunk.PageNumber,
                chunk.Content, chunk.HasUnprocessedImages) { DocumentId = chunk.DocumentId };
            if (tokens.Count(JsonSerializer.Serialize(sources.Append(source))) > options.Value.EvidenceTokens) continue;
            sources.Add(source);
        }
        if (chunks.Count > 0 && sources.Count == 0)
            throw new ChatGenerationException("Retrieved evidence exceeds the configured context budget.");
        if (sources.Count == 0) return new ChatAnswer(PromptConstants.InsufficientEvidenceAnswer, []);

        // No old assistant response or old source passage enters the evidence prompt.
        var answer = await generator.GenerateAsync(PromptConstants.ChatAnswerInstructions,
            JsonSerializer.Serialize(new { originalQuestion = message, question, sources }), cancellationToken);
        ValidateAnswer(answer, sources.Count);
        return new ChatAnswer(answer, sources);
    }

    public IReadOnlyList<ChatHistory> BoundHistory(IReadOnlyList<ChatMessage> turns)
    {
        var history = new List<ChatHistory>();
        foreach (var turn in turns.OrderByDescending(turn => turn.CreatedAtUtc).ThenByDescending(turn => turn.Id, StringComparer.Ordinal).Take(options.Value.HistoryTurns))
        {
            var pair = new ChatHistory(turn.Query, turn.Answer);
            var candidate = history.Prepend(pair).ToArray();
            if (tokens.Count(JsonSerializer.Serialize(candidate)) > options.Value.HistoryTokens) break;
            history.Insert(0, pair);
        }
        return history;
    }

    private static Resolution ReadResolution(string text, bool optimize)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var expected = optimize ? new[] { "question", "retrievalQuestion", "clarification" } : ["question", "clarification"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != expected.Length
                || !root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                throw new JsonException();
            var q = root.GetProperty("question");
            var c = root.GetProperty("clarification");
            var question = q.ValueKind == JsonValueKind.Null ? null : q.GetString()?.Trim();
            var clarification = c.ValueKind == JsonValueKind.Null ? null : c.GetString()?.Trim();
            var retrieval = optimize ? root.GetProperty("retrievalQuestion") : q;
            var retrievalQuestion = retrieval.ValueKind == JsonValueKind.Null ? null : retrieval.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(question) && question.Length <= 2000
                && !string.IsNullOrWhiteSpace(retrievalQuestion) && retrievalQuestion.Length <= 2000 && clarification is null)
                return new(question, retrievalQuestion, null);
            if (question is null && retrievalQuestion is null && !string.IsNullOrWhiteSpace(clarification)
                && clarification.Length <= 500 && clarification.EndsWith('?'))
                return new(null, null, clarification);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { }
        throw new ChatGenerationException("Query preparation returned an invalid response.");
    }

    private static bool PreservesLiterals(string original, string rewritten, bool allowAdditional)
    {
        // Conservative mechanical guards, not a semantic equivalence check.
        var originalNumbers = NumberedTerms().Matches(original).Select(match => match.Value)
            .Where(value => value.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var rewrittenNumbers = NumberedTerms().Matches(rewritten).Select(match => match.Value)
            .Where(value => value.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        return (allowAdditional ? originalNumbers.IsSubsetOf(rewrittenNumbers) : originalNumbers.SetEquals(rewrittenNumbers))
            && QuotedPhrases().Matches(original).All(match => rewritten.Contains(match.Value, StringComparison.Ordinal));
    }

    private static void ValidateAnswer(string answer, int sourceCount)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 16000)
            throw new ChatGenerationException("Answer generation returned empty or oversized text.");
        if (answer.Trim() == PromptConstants.InsufficientEvidenceAnswer) return;
        var citations = Citations().Matches(answer);
        if (citations.Count == 0 || citations.Any(match => !match.Groups[1].Value.All(char.IsAsciiDigit)
                || !int.TryParse(match.Groups[1].Value, out var n) || n < 1 || n > sourceCount))
            throw new ChatGenerationException("Answer generation returned missing or invalid source citations.");
        // This checks reference integrity only. It does not prove factual support.
    }

    [GeneratedRegex(@"\[([+-]?\d[^\]\r\n]*)\]")] private static partial Regex Citations();
    [GeneratedRegex(@"[+\-]?[$€£₹]?[\p{L}\p{N}]+(?:[.,:/_+\-][\p{L}\p{N}]+)*%?")] private static partial Regex NumberedTerms();
    [GeneratedRegex("\"([^\"\\r\\n]+)\"|“([^”\\r\\n]+)”")] private static partial Regex QuotedPhrases();
    public sealed record ChatHistory(string User, string Assistant);
    private sealed record Resolution(string? Question, string? RetrievalQuestion, string? Clarification);
}
