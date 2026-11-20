using TripConcierge.Api.Tools;
using TripConcierge.Api.Travel;
using TripConcierge.Core.Trips;
using System.Diagnostics;
using TripConcierge.Api.Telemetry;

namespace TripConcierge.Api.Workflows;

public static class TripRunEndpoints
{
    public static async Task<IResult> PutAsync(
        Guid runId,
        TripRequest? request,
        TripRunStore store,
        TripPlanningWorkflow workflow,
        ILogger<TripPlanningWorkflow> logger,
        CancellationToken cancellationToken)
    {
        var errors = TripRequestValidator.Validate(request);
        if (runId == Guid.Empty)
        {
            errors["runId"] = ["A non-empty run ID is required."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        Activity.Current?.SetTag("trip.run.id", runId.ToString("D"));
        var selectedId = request!.SelectedDestinationId;
        var originalRequest = request with { SelectedDestinationId = null };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selectedId is null)
            {
                store.CreateIfMissing(runId, originalRequest);
            }

            var saved = store.Find(runId);
            if (saved is null)
            {
                return Results.NotFound(new { error = "Start this trip run first." });
            }

            if (saved.Request != originalRequest)
            {
                return Results.Conflict(new
                {
                    error = "This run ID belongs to a different trip request."
                });
            }

            DestinationLookup? savedDestination = null;

            if (selectedId is null)
            {
                if (saved.Draft is { } current)
                {
                    TripTelemetry.RecordRunReplay();
                    return Results.Ok(current);
                }
            }
            else
            {
                if (saved.SelectedDestinationId is { } previousId &&
                    previousId != selectedId)
                {
                    return Results.Conflict(new
                    {
                        error = "This run already has a different destination selection."
                    });
                }

                if (saved.Draft is not { } checkpoint)
                {
                    return Results.Conflict(new
                    {
                        error = "Retry after this run returns destination candidates."
                    });
                }

                if (checkpoint.Status != "awaiting_destination_selection")
                {
                    TripTelemetry.RecordRunReplay();
                    return saved.SelectedDestinationId == selectedId
                        ? Results.Ok(checkpoint)
                        : Results.Conflict(new
                        {
                            error = "This run is no longer awaiting a selection."
                        });
                }

                if (!checkpoint.DestinationLookup.Candidates
                    .Any(candidate => candidate.Id == selectedId))
                {
                    return Results.ValidationProblem(
                        new Dictionary<string, string[]>
                        {
                            ["selectedDestinationId"] =
                                ["Select one of the saved destination candidates."]
                        });
                }

                savedDestination = checkpoint.DestinationLookup;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!store.TryClaim(runId, saved.Version, selectedId))
            {
                return Results.Conflict(new
                {
                    error = "The run is active or has just advanced. Retry the same request."
                });
            }

            var claimedVersion = saved.Version + 1;
            var committed = false;

            try
            {
                var checkpoint = store.LoadCheckpoint(runId);

                var draft = await workflow.RunAsync(
                    request,
                    runId,
                    savedDestination,
                    cancellationToken,
                    checkpoint,
                    next =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!store.TrySaveCheckpoint(runId, claimedVersion, next))
                        {
                            throw new TripRunConflictException(
                                "Another attempt took ownership of this run.");
                        }
                    });

                if (draft.Status == "destination_lookup_unavailable")
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Destination lookup unavailable. Retry this run.");
                }

                committed = store.TrySave(runId, claimedVersion, draft);

                return committed
                    ? Results.Ok(draft)
                    : Results.Conflict(new
                    {
                        error = "Another attempt took over. Retry the same request."
                    });
            }
            finally
            {
                if (!committed)
                {
                    store.Release(runId, claimedVersion);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(statusCode: 504, title: "Trip planning timed out");
        }
        catch (TripRunConflictException exception)
        {
            return Results.Conflict(new
            {
                error = exception.Message
            });
        }
        catch (TripRunException exception)
        {
            return Results.Problem(
                statusCode: 502,
                title: "Agent planning did not complete",
                detail: exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Trip run {RunId} failed. Exception type {ExceptionType}",
                runId, exception.GetType().Name);

            return Results.Problem(
                statusCode: 503,
                title: "Trip planning is temporarily unavailable");
        }
    }
}