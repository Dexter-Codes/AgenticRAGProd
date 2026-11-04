using TripConcierge.Core.Common;

namespace TripConcierge.Core.Budgeting;

public enum CostBasis
{
    PerTrip,
    PerTraveler,
    PerTravelerPerDay,
    PerTravelerPerNight
}

public sealed record TravelOption(
    string Id,
    string Description,
    Money? UnitPrice,
    CostBasis Basis,
    string PriceSource);

public sealed record BudgetLine(
    string OptionId,
    string Description,
    decimal Quantity,
    Money UnitPrice,
    Money Total,
    string PriceSource);

public sealed record BudgetBreakdown(
    Money Budget,
    Money Total,
    Money Remaining,
    bool IsWithinBudget,
    IReadOnlyList<BudgetLine> Lines);