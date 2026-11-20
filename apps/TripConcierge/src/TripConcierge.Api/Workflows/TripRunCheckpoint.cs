using TripConcierge.Api.Agents;
using TripConcierge.Api.Travel;

namespace TripConcierge.Api.Workflows;

public sealed record TripRunCheckpoint(
    string LastCompletedStage = "created",
    DestinationLookup? Destination = null,
    WeatherLookup? Weather = null,
    TripDraft? BudgetDraft = null,
    TravelKnowledgeLookup? Knowledge = null,
    ItineraryPlan? Itinerary = null);

public sealed class TripRunConflictException(string message)
    : Exception(message);