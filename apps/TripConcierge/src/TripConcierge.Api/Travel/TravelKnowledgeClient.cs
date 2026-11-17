using System.Net.Http.Json;
using System.Text.Json;

namespace TripConcierge.Api.Travel;

public sealed record TravelPassage(
    string ChunkId,
    Guid DocumentId,
    string DocumentName,
    int PageNumber,
    string Passage,
    bool IsTruncated,
    bool HasUnprocessedImages,
    JsonElement? Context);

public sealed record TravelKnowledgeLookup(
    string Status,
    int DestinationId,
    IReadOnlyList<TravelPassage> Passages);

public sealed class TravelKnowledgeClient(
    HttpClient httpClient,
    ILogger<TravelKnowledgeClient> logger)
{
    public async Task<TravelKnowledgeLookup> SearchAsync(
        DestinationCandidate destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TravelKnowledgeLookup Empty(string status) =>
            new(status, destination.Id, []);

        var request = new
        {
            destinationId = destination.Id,
            query =
                $"Visitor attractions in {destination.Name}, " +
                $"{destination.Country}: names and descriptions."
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "api/travel-knowledge/search",
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Travel knowledge request failed; HTTP status {StatusCode}",
                    (int)response.StatusCode);

                return Empty("unavailable");
            }

            var result = await response.Content
                .ReadFromJsonAsync<TravelKnowledgeLookup>(
                    cancellationToken: cancellationToken);

            if (!IsValid(result, destination.Id))
            {
                logger.LogWarning(
                    "Travel knowledge returned an invalid response.");

                return Empty("invalid_response");
            }

            logger.LogInformation(
                "Travel knowledge returned {PassageCount} passages",
                result!.Passages.Count);

            return result;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Travel knowledge request timed out.");
            return Empty("unavailable");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Travel knowledge service could not be reached.");
            return Empty("unavailable");
        }
        catch (JsonException)
        {
            logger.LogWarning("Travel knowledge returned invalid JSON.");
            return Empty("invalid_response");
        }
    }

    private static bool IsValid(
        TravelKnowledgeLookup? result,
        int expectedDestinationId)
    {
        if (result is null ||
            result.DestinationId != expectedDestinationId ||
            result.Passages is null ||
            result.Passages.Count > 5)
        {
            return false;
        }

        if (result.Status == "no_evidence")
        {
            return result.Passages.Count == 0;
        }

        if (result.Status != "retrieved" ||
            result.Passages.Count == 0)
        {
            return false;
        }

        if (result.Passages.Any(passage =>
                passage is null ||
                string.IsNullOrWhiteSpace(passage.ChunkId) ||
                passage.DocumentId == Guid.Empty ||
                string.IsNullOrWhiteSpace(passage.DocumentName) ||
                passage.PageNumber < 1 ||
                string.IsNullOrWhiteSpace(passage.Passage) ||
                passage.Passage.Length > 3000))
        {
            return false;
        }

        return result.Passages
            .Select(passage => passage.ChunkId)
            .Distinct(StringComparer.Ordinal)
            .Count() == result.Passages.Count;
    }
}