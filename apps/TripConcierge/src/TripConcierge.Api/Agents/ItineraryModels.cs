using TripConcierge.Api.Travel;
using TripConcierge.Core.Budgeting;
using TripConcierge.Core.Trips;

namespace TripConcierge.Api.Agents;

public sealed record ItineraryPlanningInput(
    TripRequest Trip,
    DestinationCandidate Destination,
    BudgetBreakdown Budget,
    WeatherLookup Weather);

public sealed record ItineraryDay(
    DateOnly Date,
    string MorningActivityId,
    string AfternoonActivityId,
    string EveningActivityId);

public sealed record ItineraryOutput(
    ItineraryDay[]? Days);

public sealed record ItineraryPlan(
    string Status,
    IReadOnlyList<ItineraryDay> Days,
    IReadOnlyDictionary<string, string> ActivityCatalog,
    string Notice);