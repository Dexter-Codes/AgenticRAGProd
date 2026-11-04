using System.Net.Http.Json;
using System.Text.Json;

namespace TripConcierge.Api.Travel;

public sealed record DestinationCandidate(
    int Id,
    string Name,
    double Latitude,
    double Longitude,
    string? Country,
    string? Admin1,
    string? Timezone);

public sealed record DestinationLookup(
    string Status,
    IReadOnlyList<DestinationCandidate> Candidates,
    string Attribution,
    DateTimeOffset CheckedAt,
    DestinationCandidate? SelectedDestination = null);

public sealed class DestinationSearchClient(
    HttpClient httpClient,
    ILogger<DestinationSearchClient> logger)
{
    public sealed record SearchResponse(
        DestinationCandidate[]? Results);

    public async Task<DestinationLookup> SearchAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var path =
            $"v1/search?name={Uri.EscapeDataString(destination.Trim())}" +
            "&count=3&language=en&format=json";

        try
        {
            var response =
                await httpClient.GetFromJsonAsync<SearchResponse>(
                    path,
                    cancellationToken);

            if (response is null)
            {
                return Result("unavailable", []);
            }

            var candidates = response.Results ?? [];

            return Result(
                candidates.Length == 0 ? "no_matches" : "candidates_found",
                candidates);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Destination provider timed out.");
            return Result("unavailable", []);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "Destination provider failed; HTTP status {StatusCode}",
                exception.StatusCode);

            return Result("unavailable", []);
        }
        catch (JsonException)
        {
            logger.LogWarning("Destination provider returned invalid JSON.");
            return Result("unavailable", []);
        }
    }

    private static DestinationLookup Result(
        string status,
        IReadOnlyList<DestinationCandidate> candidates) =>
        new(
            status,
            candidates,
            "Location data: GeoNames, via Open-Meteo. " +
            "https://open-meteo.com/en/docs/geocoding-api",
            DateTimeOffset.UtcNow);
}