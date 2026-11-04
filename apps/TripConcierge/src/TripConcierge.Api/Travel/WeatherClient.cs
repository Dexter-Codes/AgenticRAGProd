using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripConcierge.Api.Travel;

public sealed record WeatherDay(
    DateOnly Date,
    double? MaximumTemperatureC,
    double? MinimumTemperatureC);

public sealed record WeatherLookup(
    string Status,
    int? DestinationId,
    IReadOnlyList<WeatherDay> Days,
    string Attribution,
    DateTimeOffset CheckedAt,
    DateOnly? AvailableFrom = null,
    DateOnly? AvailableThrough = null,
    string? Timezone = null);

public sealed class WeatherClient(
    HttpClient httpClient,
    ILogger<WeatherClient> logger)
{
    public sealed record ForecastResponse(
        DailyForecast? Daily,
        string? Timezone);

    public sealed record DailyForecast(
        DateOnly[]? Time,

        [property: JsonPropertyName("temperature_2m_max")]
        double?[]? MaximumC,

        [property: JsonPropertyName("temperature_2m_min")]
        double?[]? MinimumC);

    public async Task<WeatherLookup> GetAsync(
        DestinationCandidate destination,
        DateOnly departureDate,
        DateOnly returnDate,
        CancellationToken cancellationToken)
    {
        WeatherLookup Result(
            string status,
            IReadOnlyList<WeatherDay>? days = null,
            DateOnly? from = null,
            DateOnly? through = null,
            string? timezone = null) =>
            new(
                status,
                destination.Id,
                days ?? [],
                "Weather data: Open-Meteo. https://open-meteo.com/",
                DateTimeOffset.UtcNow,
                from,
                through,
                timezone);

        var latitude = destination.Latitude.ToString(
            "R", CultureInfo.InvariantCulture);

        var longitude = destination.Longitude.ToString(
            "R", CultureInfo.InvariantCulture);

        var timezone = Uri.EscapeDataString(
            destination.Timezone ?? "auto");

        var path =
            $"v1/forecast?latitude={latitude}&longitude={longitude}" +
            "&forecast_days=16&temperature_unit=celsius" +
            "&daily=temperature_2m_max,temperature_2m_min" +
            $"&timezone={timezone}";

        try
        {
            var response =
                await httpClient.GetFromJsonAsync<ForecastResponse>(
                    path,
                    cancellationToken);

            var daily = response?.Daily;

            if (daily?.Time is null ||
                daily.MaximumC is null ||
                daily.MinimumC is null ||
                daily.Time.Length == 0 ||
                daily.MaximumC.Length != daily.Time.Length ||
                daily.MinimumC.Length != daily.Time.Length)
            {
                return Result("unavailable");
            }

            var from = daily.Time.Min();
            var through = daily.Time.Max();

            if (departureDate < from || returnDate > through)
            {
                return Result(
                    "outside_forecast_window",
                    from: from,
                    through: through,
                    timezone: response!.Timezone);
            }

            var days = daily.Time
                .Select((date, index) => new WeatherDay(
                    date,
                    daily.MaximumC[index],
                    daily.MinimumC[index]))
                .Where(day =>
                    day.Date >= departureDate &&
                    day.Date <= returnDate)
                .OrderBy(day => day.Date)
                .ToArray();

            var expectedDays =
                returnDate.DayNumber - departureDate.DayNumber + 1;

            var complete =
                days.Select(day => day.Date).Distinct().Count()
                    == expectedDays &&
                days.All(day =>
                    day.MaximumTemperatureC.HasValue &&
                    day.MinimumTemperatureC.HasValue);

            return Result(
                complete ? "available" : "partial_data",
                days,
                from,
                through,
                response!.Timezone);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Weather provider timed out.");
            return Result("unavailable");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "Weather provider failed; HTTP status {StatusCode}",
                exception.StatusCode);

            return Result("unavailable");
        }
        catch (JsonException)
        {
            logger.LogWarning("Weather provider returned invalid JSON.");
            return Result("unavailable");
        }
    }
}