using System.ComponentModel;
using System.Text.Json;
using TripConcierge.Core.Budgeting;
using TripConcierge.Core.Trips;
using TripConcierge.Api.Travel;
using TripConcierge.Core.Common;

namespace TripConcierge.Api.Tools;

public sealed class TripTools(
    TripRequest trip,
    ILogger logger,
    DestinationSearchClient destinationSearch,
    WeatherClient weatherClient,
    TripOptionCatalog catalog,
    CancellationToken runToken)
{
    private sealed record DemoOffer(
        string Category,
        TravelOption Option);

    private static DemoOffer Offer(
        string category,
        string id,
        decimal price,
        CostBasis basis) =>
        new(category, new TravelOption(
            id,
            $"Fictional {id}",
            new Money(price, "INR"),
            basis,
            "demo-fixture-v1"));

   private readonly DemoOffer[] _offers = catalog.Transport
    .Select(option => new DemoOffer("transport", option))
    .Concat(catalog.Lodging.Select(
        option => new DemoOffer("lodging", option)))
    .Concat(catalog.Food.Select(
        option => new DemoOffer("food", option)))
    .ToArray();
    
    private int _calls;
    private bool _catalogRead;

    public int Calls => Volatile.Read(ref _calls);

    private string[]? _initialSelectedIds;

    public string[]? InitialSelectedIds =>
        Volatile.Read(ref _initialSelectedIds);

    private void Enter(string toolName)
    {
        runToken.ThrowIfCancellationRequested();

        var count = Interlocked.Increment(ref _calls);

        if (count > 8)
            throw new TripRunException(
                "The tool-call limit was exceeded.");

        logger.LogInformation(
            "Executing tool {ToolName}; attempt {Attempt}",
            toolName,
            count);
    }

    [Description(
        "List fictional demo travel options. Prices are not live quotes. " +
        "Choose exactly one option from transport, lodging and food.")]
    public string GetDemoTravelOptions()
    {
        Enter(nameof(GetDemoTravelOptions));
        _catalogRead = true;

       // In GetDemoTravelOptions:
        return JsonSerializer.Serialize(_offers.Select(offer => new
        {
            offer.Category,
            offer.Option.Id,
            offer.Option.Description,
            offer.Option.UnitPrice,
            Basis = offer.Option.Basis.ToString(),
            offer.Option.PriceSource
        }));
    }

    [Description(
        "Calculate the selected options against the user's fixed trip " +
        "and budget. Supply exactly three catalog IDs: one transport, " +
        "one lodging and one food option. Prices cannot be supplied.")]
    public BudgetBreakdown CalculateBudget(
        [Description("The three selected catalog option IDs.")]
        string[] optionIds)
    {
        Enter(nameof(CalculateBudget));

        if (!_catalogRead)
            throw new TripRunException(
                "Read the option catalog before calculating.");

        var selected = ResolveSelection(optionIds);
        var result = BudgetCalculator.Calculate(trip, selected);

       if (Interlocked.CompareExchange(
        ref _initialSelectedIds,
        optionIds.ToArray(),
        null) is not null)
        {
            throw new TripRunException(
                "An initial budget selection has already been submitted.");
        }

        return result;
    }

    public TravelOption[] ResolveSelection(
        IReadOnlyList<string>? ids)
    {
        if (ids is null ||
            ids.Count != 3 ||
            ids.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new TripRunException(
                "Select exactly three distinct option IDs.");
        }

      // In ResolveSelection:
        var selected = ids.Select(id =>
            _offers.FirstOrDefault(offer => offer.Option.Id == id)
            ?? throw new TripRunException(
                "An unknown option ID was selected."))
            .ToArray();

        foreach (var category in new[]
                 { "transport", "lodging", "food" })
        {
            if (selected.Count(x => x.Category == category) != 1)
                throw new TripRunException(
                    $"Select exactly one {category} option.");
        }

        return selected.Select(x => x.Option).ToArray();
    }

    public DestinationLookup? LastDestinationLookup { get; private set; }

  [Description(
    "Look up the trip destination and validate the user's selected " +
    "destination ID against the returned candidates. " +
    "Never choose a destination on the user's behalf.")]
    public async Task<DestinationLookup> LookupDestination(DestinationLookup? savedDestination = null)
    {
        Enter(nameof(LookupDestination));

        if (LastDestinationLookup is { } existing)
        {
            return existing;
        }

       var result = savedDestination is { } saved
    ? saved with { Status = "candidates_found" }
    : await destinationSearch.SearchAsync(trip.Destination, runToken);

        if (result.Status == "candidates_found")
        {
            var selected = trip.SelectedDestinationId is { } selectedId
                ? result.Candidates.FirstOrDefault(
                    candidate => candidate.Id == selectedId)
                : null;

            var status = selected is not null
                ? "destination_confirmed"
                : trip.SelectedDestinationId.HasValue
                    ? "invalid_selection"
                    : "selection_required";

            result = result with
            {
                Status = status,
                SelectedDestination = selected
            };
        }

        LastDestinationLookup = result;
        return result;
    }


    public WeatherLookup? LastWeatherLookup { get; private set; }

    [Description(
        "Get weather for the user's confirmed destination and trip dates. " +
        "Call LookupDestination first. " +
        "Weather may require destination confirmation, fall outside the " +
        "forecast window, contain partial data, or be unavailable.")]
    public async Task<WeatherLookup> GetDestinationWeather()
    {
        Enter(nameof(GetDestinationWeather));

        if (LastWeatherLookup is { } existing)
        {
            return existing;
        }

        if (LastDestinationLookup is null)
        {
            throw new TripRunException(
                "Look up the destination before requesting weather.");
        }

        WeatherLookup result;

        if (LastDestinationLookup.SelectedDestination is not { } destination)
        {
            result = new WeatherLookup(
                "destination_confirmation_required",
                null,
                [],
                "Weather was not requested from the provider.",
                DateTimeOffset.UtcNow);
        }
        else
        {
            result = await weatherClient.GetAsync(
                destination,
                trip.DepartureDate,
                trip.ReturnDate,
                runToken);
        }

        LastWeatherLookup = result;
        return result;
    }

    public void RestoreWeather(WeatherLookup weather)
    {
        runToken.ThrowIfCancellationRequested();

        var destinationId =
            LastDestinationLookup?.SelectedDestination?.Id;

        if (destinationId is null ||
            weather.DestinationId != destinationId)
        {
            throw new TripRunException(
                "Saved weather does not match the confirmed destination.");
        }

        LastWeatherLookup = weather;
    }

}

public sealed class TripRunException(string message)
    : Exception(message);