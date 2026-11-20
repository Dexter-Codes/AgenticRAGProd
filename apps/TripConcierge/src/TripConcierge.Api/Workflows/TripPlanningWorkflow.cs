using TripConcierge.Api.Agents;
using TripConcierge.Api.Tools;
using TripConcierge.Api.Travel;
using TripConcierge.Core.Trips;
using System.Diagnostics;
using TripConcierge.Api.Telemetry;

namespace TripConcierge.Api.Workflows;

public sealed class TripPlanningWorkflow(
    TripCoordinator coordinator,
    DestinationSearchClient destinationSearch,
    WeatherClient weatherClient,
    ItineraryPlanner itineraryPlanner,
    TravelKnowledgeClient travelKnowledge,
    ILogger<TripPlanningWorkflow> logger)
{

    public Task<TripDraft> RunAsync(
    TripRequest request,
    Guid runId,
    DestinationLookup? savedDestination,
    CancellationToken cancellationToken,
    TripRunCheckpoint? checkpoint = null,
    Action<TripRunCheckpoint>? saveCheckpoint = null) =>
    TripTelemetry.MeasureAsync(
        "workflow",
        async () =>
        {
            Activity.Current?.SetTag(
                "trip.run.id", runId.ToString("D"));

            return await RunCoreAsync(
                request,
                runId,
                savedDestination,
                cancellationToken,
                checkpoint,
                saveCheckpoint);
        },
        cancellationToken,
        draft => draft.Status);

    public Task<TripDraft> RunAsync(
    TripRequest request,
    CancellationToken cancellationToken) =>
    RunAsync(request, Guid.NewGuid(), null, cancellationToken);
    private async Task<TripDraft> RunCoreAsync(
    TripRequest request,
    Guid runId,
    DestinationLookup? savedDestination,
    CancellationToken cancellationToken,
    TripRunCheckpoint? checkpoint = null,
    Action<TripRunCheckpoint>? saveCheckpoint = null)
{
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

        var progress = checkpoint ?? new TripRunCheckpoint();

        void SaveProgress(TripRunCheckpoint next)
        {
            token.ThrowIfCancellationRequested();

            saveCheckpoint?.Invoke(next);
            progress = next;

            logger.LogInformation(
                "Workflow completed stage {Stage}",
                next.LastCompletedStage);
        }

       var catalog = new TripOptionCatalog(
        DemoOptionSources.GetTransportOptions(),
        DemoOptionSources.GetLodgingOptions(),
        DemoOptionSources.GetFoodOptions());

        var tools = new TripTools(
        request,
        logger,
        destinationSearch,
        weatherClient,
        catalog,
        token);

        logger.LogInformation(
            "Workflow stage {Stage}", "destination_lookup");
        
        if (progress.Destination is not null)
        {
            TripTelemetry.RecordCheckpointReuse("destination_lookup");
        }

        var destination = await TripTelemetry.MeasureAsync(
        "destination_lookup",
        () => tools.LookupDestination(
            progress.Destination ?? savedDestination),
        token,
        result => result.Status);

        token.ThrowIfCancellationRequested();

        if (destination.Candidates.Count > 0 &&
            (progress.Destination is null ||
            progress.Destination.SelectedDestination?.Id !=
                destination.SelectedDestination?.Id))
        {
            SaveProgress(progress with
            {
                Destination = destination,
                LastCompletedStage = "destination_lookup"
            });
        }

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

        var weather = progress.Weather;

        if (weather is null)
        {
            logger.LogInformation(
                "Workflow stage {Stage}", "weather_lookup");
            
            TripTelemetry.RecordCheckpointReuse("weather_lookup");
            
            weather = await TripTelemetry.MeasureAsync(
            "weather_lookup",
            () => tools.GetDestinationWeather(),
            token,
            result => result.Status);

            SaveProgress(progress with
            {
                Weather = weather,
                LastCompletedStage = "weather_lookup"
            });
        }
        else
        {
            tools.RestoreWeather(weather);

            logger.LogInformation(
                "Reusing saved stage {Stage}", "weather_lookup");
        }
        logger.LogInformation(
            "Weather lookup completed with status {WeatherStatus}",
            weather.Status);

        token.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Workflow stage {Stage}", "budget_planning");

       var draft = progress.BudgetDraft;

        if (draft is null)
        {
            logger.LogInformation(
                "Workflow stage {Stage}", "budget_planning");
            
            TripTelemetry.RecordCheckpointReuse("budget_planning");
            
            draft = await TripTelemetry.MeasureAsync(
            "budget_planning",
            () => coordinator.CreateDraftAsync(request, runId, tools, token),
            token,
            result => result.Status);

            if (draft.Budget is { IsWithinBudget: false } initialBudget)
            {
                var (revisedBudget, revision) = BudgetSupervisor.Revise(
                    request,
                    catalog,
                    initialBudget);

                draft = draft with
                {
                    Budget = revisedBudget,
                    BudgetRevision = revision,
                    Status = revisedBudget.IsWithinBudget
                        ? "within_budget_demo"
                        : "over_budget_selection"
                };
            }

            SaveProgress(progress with
            {
                BudgetDraft = draft,
                LastCompletedStage = "budget_planning"
            });
        }
        else
        {
            logger.LogInformation(
                "Reusing saved stage {Stage}", "budget_planning");
        }

        if (draft.Budget is not { IsWithinBudget: true } budget)
        {
            return draft;
        }

        var confirmedDestination = destination.SelectedDestination!;

        logger.LogInformation(
            "Workflow stage {Stage}", "travel_knowledge");

        var knowledge = progress.Knowledge;

        if (knowledge is null)
        {
            logger.LogInformation(
                "Workflow stage {Stage}", "travel_knowledge");
            
            TripTelemetry.RecordCheckpointReuse("travel_knowledge");
            
            knowledge = await TripTelemetry.MeasureAsync(
            "travel_knowledge",
            () => travelKnowledge.SearchAsync(confirmedDestination, token),
            token,
            result => result.Status);

            SaveProgress(progress with
            {
                Knowledge = knowledge,
                LastCompletedStage = "travel_knowledge"
            });
        }
        else
        {
            logger.LogInformation(
                "Reusing saved stage {Stage}", "travel_knowledge");
        }

        token.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Travel knowledge completed with status {KnowledgeStatus}",
            knowledge.Status);

        logger.LogInformation(
            "Workflow stage {Stage}", "itinerary_planning");

        var itinerary = progress.Itinerary;

        if (itinerary is null)
        {
            logger.LogInformation(
                "Workflow stage {Stage}", "itinerary_planning");

            var handoff = new ItineraryPlanningInput(
                request,
                confirmedDestination,
                budget,
                weather,
                knowledge);
            
            TripTelemetry.RecordCheckpointReuse("itinerary_planning");
            
            itinerary = await TripTelemetry.MeasureAsync(
            "itinerary_planning",
            () => itineraryPlanner.PlanAsync(handoff, token),
            token,
            result => result.Status);

            SaveProgress(progress with
            {
                Itinerary = itinerary,
                LastCompletedStage = "itinerary_planning"
            });
        }
        else
        {
            logger.LogInformation(
                "Reusing saved stage {Stage}", "itinerary_planning");
        }

        return draft with
        {
            Itinerary = itinerary,
            Knowledge = knowledge
        };
    }
}