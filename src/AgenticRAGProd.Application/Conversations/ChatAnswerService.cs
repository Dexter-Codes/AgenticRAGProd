using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Prompts;

namespace AgenticRAGProd.Application.Conversations;

public sealed partial class ChatAnswerService(
    IAnswerGenerator generator, ITextEmbeddingService embeddings, ICorpusChunkRetriever retriever,
    ITextTokenCounter tokens, IOptions<ChatOptions> options, ChatTelemetry telemetry)
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
            var resolved = await telemetry.MeasureAsync(
                "chat.query_preparation",
                async activity =>
                {
                    var history = BoundHistory(completed);

                    activity?.SetTag(
                        "rag.history.included_turns",
                        history.Count);

                    activity?.SetTag(
                        "rag.history.omitted",
                        earlierHistoryOmitted ||
                        history.Count < completed.Length);

                    activity?.SetTag(
                        "rag.query.optimization.enabled",
                        optimize);

                    var instructions = optimize
                        ? PromptConstants.QueryOptimizationInstructions
                        : PromptConstants.QueryResolutionInstructions;

                    var input = JsonSerializer.Serialize(new
                    {
                        message,
                        history,
                        historyIsBounded = true,
                        historyOmitted =
                            earlierHistoryOmitted ||
                            history.Count < completed.Length
                    });

                    var resolution = await generator.GenerateAsync(
                        instructions,
                        input,
                        cancellationToken);

                    var prepared = ReadResolution(resolution, optimize);

                    if (prepared.Clarification is null)
                    {
                        var standaloneQuestion = completed.Length == 0
                            ? message.Trim()
                            : prepared.Question!;

                        if (!PreservesLiterals(
                            message,
                            standaloneQuestion,
                            allowAdditional: true))
                        {
                            throw new ChatGenerationException(
                                "Query preparation changed an explicit " +
                                "number or quoted phrase.");
                        }
                    }

                    return prepared;
                });

           if (resolved.Clarification is not null)
            {
                return new ChatAnswer(
                    resolved.Clarification,
                    [],
                    ChatAnswerKind.Clarification);
            }

            question = completed.Length == 0
                ? message.Trim()
                : resolved.Question!;

            retrievalQuestion =
                optimize &&
                PreservesLiterals(
                    question,
                    resolved.RetrievalQuestion!,
                    allowAdditional: false)
                ? resolved.RetrievalQuestion!
                : question;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var vector = await telemetry.MeasureAsync(
        "chat.embedding",
        () => embeddings.GenerateAsync(
            retrievalQuestion,
            cancellationToken));

        var chunks = await telemetry.MeasureAsync(
        "chat.search",
        async activity =>
        {
            var results = await retriever.RetrieveCorpusAsync(
                retrievalQuestion,
                vector,
                cancellationToken);

            activity?.SetTag(
                "rag.retrieval.returned_chunks",
                results.Count);

            return results;
        });
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

        if (sources.Count == 0)
        {
            return new ChatAnswer(
                PromptConstants.InsufficientEvidenceAnswer,
                [],
                ChatAnswerKind.InsufficientEvidence);
        }

        // No old assistant response or old source passage enters the evidence prompt.
       return await telemetry.MeasureAsync(
            "chat.answer_generation",
            async activity =>
            {
                activity?.SetTag(
                    "rag.evidence.source_count",
                    sources.Count);

                var input = JsonSerializer.Serialize(new
                {
                    originalQuestion = message,
                    question,
                    sources
                });

                var text = await generator.GenerateAsync(
                    PromptConstants.ChatAnswerInstructions,
                    input,
                    cancellationToken);

                ValidateAnswer(text, sources.Count);

                var kind = string.Equals(
                    text.Trim(),
                    PromptConstants.InsufficientEvidenceAnswer,
                    StringComparison.Ordinal)
                        ? ChatAnswerKind.InsufficientEvidence
                        : ChatAnswerKind.Answered;

                activity?.SetTag(
                    "rag.answer.kind",
                    ChatTelemetry.AnswerKindTag(kind));

                return new ChatAnswer(text, sources, kind);
            });
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
