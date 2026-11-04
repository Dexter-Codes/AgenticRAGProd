using TripConcierge.Api.Agents;
using TripConcierge.Api.Tools;
using TripConcierge.Api.Travel;
using TripConcierge.Core.Trips;

namespace TripConcierge.Api.Workflows;

public sealed class TripPlanningWorkflow(
    TripCoordinator coordinator,
    DestinationSearchClient destinationSearch,
    WeatherClient weatherClient,
    ItineraryPlanner itineraryPlanner,
    ILogger<TripPlanningWorkflow> logger)
{
    public async Task<TripDraft> RunAsync(
        TripRequest request,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();

        using var scope = logger.BeginScope(
            new Dictionary<string, object>
            {
                ["RunId"] = runId
            });

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var token = timeout.Token;

        var tools = new TripTools(
            request,
            logger,
            destinationSearch,
            weatherClient,
            token);

        logger.LogInformation(
            "Workflow stage {Stage}", "destination_lookup");

        var destination = await tools.LookupDestination();

        token.ThrowIfCancellationRequested();

        if (destination.SelectedDestination is null)
        {
            var status = destination.Status switch
            {
                "selection_required" =>
                    "awaiting_destination_selection",

                "invalid_selection" =>
                    "invalid_destination_selection",

                "no_matches" =>
                    "destination_not_found",

                _ => "destination_lookup_unavailable"
            };

            logger.LogInformation(
                "Workflow stopped with status {Status}", status);

            return new TripDraft(
                runId,
                status,
                null,
                destination,
                null);
        }

        logger.LogInformation(
            "Workflow stage {Stage}", "weather_lookup");

        var weather = await tools.GetDestinationWeather();

        logger.LogInformation(
            "Weather lookup completed with status {WeatherStatus}",
            weather.Status);

        token.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Workflow stage {Stage}", "budget_planning");

        var draft = await coordinator.CreateDraftAsync(
            request,
            runId,
            tools,
            token);

        // Resolve an over-budget selection before planning activities.
        if (draft.Budget is not { IsWithinBudget: true } budget)
        {
            logger.LogInformation(
                "Itinerary skipped because the selected options exceed the budget.");

            return draft;
        }

        logger.LogInformation(
            "Workflow stage {Stage}", "itinerary_planning");

        var handoff = new ItineraryPlanningInput(
            request,
            destination.SelectedDestination!,
            budget,
            weather);

        var itinerary = await itineraryPlanner.PlanAsync(handoff, token);

        return draft with
        {
            Itinerary = itinerary
        };
    }
}