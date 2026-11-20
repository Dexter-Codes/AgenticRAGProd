using TripConcierge.Core.Budgeting;
using TripConcierge.Core.Common;
using TripConcierge.Core.Trips;

namespace TripConcierge.Api.Travel;

public sealed record BudgetOptionChange(
    string PreviousOptionId,
    string ReplacementOptionId);

public sealed record BudgetRevision(
    string Status,
    Money InitialTotal,
    IReadOnlyList<BudgetOptionChange> Changes);

public static class BudgetSupervisor
{
    public static (BudgetBreakdown Budget, BudgetRevision Revision) Revise(
        TripRequest request,
        TripOptionCatalog catalog,
        BudgetBreakdown initial)
    {
        var initialIds = initial.Lines
            .Select(line => line.OptionId)
            .ToHashSet(StringComparer.Ordinal);

        var revisedOptions = new List<TravelOption>();
        var changes = new List<BudgetOptionChange>();

        foreach (var category in new[]
                 {
                     catalog.Transport,
                     catalog.Lodging,
                     catalog.Food
                 })
        {
            var original = category.Single(
                option => initialIds.Contains(option.Id));

            var cheapest = category
                .OrderBy(option =>
                    BudgetCalculator.Calculate(request, [option]).Total.Amount)
                .ThenByDescending(option => option.Id == original.Id)
                .First();

            revisedOptions.Add(cheapest);

            if (cheapest.Id != original.Id)
            {
                changes.Add(new BudgetOptionChange(
                    original.Id,
                    cheapest.Id));
            }
        }

        if (changes.Count == 0)
        {
            return (initial, new BudgetRevision(
                "no_cheaper_options",
                initial.Total,
                []));
        }

        var revised = BudgetCalculator.Calculate(request, revisedOptions);

        return (revised, new BudgetRevision(
            revised.IsWithinBudget ? "revised_within_budget" : "still_over_budget",
            initial.Total,
            changes.AsReadOnly()));
    }
}