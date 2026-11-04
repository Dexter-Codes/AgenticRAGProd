using TripConcierge.Core.Common;
using TripConcierge.Core.Trips;

namespace TripConcierge.Core.Budgeting;

public static class BudgetCalculator
{
    public static BudgetBreakdown Calculate(
        TripRequest request,
        IReadOnlyList<TravelOption> selectedOptions)
    {
        var errors = TripRequestValidator.Validate(request);

        if (errors.Count > 0)
            throw new ArgumentException(
                "The trip request is invalid.",
                nameof(request));

        ArgumentNullException.ThrowIfNull(selectedOptions);

        if (selectedOptions.Count == 0)
            throw new ArgumentException(
                "Select at least one travel option.",
                nameof(selectedOptions));

        var currency = request.Budget.Currency
            .Trim()
            .ToUpperInvariant();

        var nights = request.ReturnDate.DayNumber
            - request.DepartureDate.DayNumber;

        var days = nights + 1;

        var lines = new List<BudgetLine>();
        var selectedIds = new HashSet<string>(
            StringComparer.Ordinal);

        decimal total = 0;

        foreach (var option in selectedOptions)
        {
            ArgumentNullException.ThrowIfNull(option);

            if (string.IsNullOrWhiteSpace(option.Id) ||
                !selectedIds.Add(option.Id))
            {
                throw new ArgumentException(
                    "Option IDs must be nonempty and unique.");
            }

            if (option.UnitPrice is null)
                throw new ArgumentException(
                    $"Option '{option.Id}' has no price.");

            if (option.UnitPrice.Amount < 0)
                throw new ArgumentException(
                    $"Option '{option.Id}' has a negative price.");

            if (!string.Equals(
                    option.UnitPrice.Currency?.Trim(),
                    currency,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Option '{option.Id}' uses a different currency.");
            }

            if (string.IsNullOrWhiteSpace(option.PriceSource))
                throw new ArgumentException(
                    $"Option '{option.Id}' needs a price source.");

            decimal quantity = option.Basis switch
            {
                CostBasis.PerTrip => 1m,

                CostBasis.PerTraveler =>
                    request.TravelerCount,

                CostBasis.PerTravelerPerDay =>
                    (decimal)request.TravelerCount * days,

                CostBasis.PerTravelerPerNight =>
                    (decimal)request.TravelerCount * nights,

                _ => throw new ArgumentException(
                    "Unsupported cost basis.")
            };

            var lineTotal = checked(
                option.UnitPrice.Amount * quantity);

            total = checked(total + lineTotal);

            lines.Add(new BudgetLine(
                option.Id,
                option.Description,
                quantity,
                new Money(option.UnitPrice.Amount, currency),
                new Money(lineTotal, currency),
                option.PriceSource));
        }

        return new BudgetBreakdown(
            Budget: new Money(request.Budget.Amount, currency),
            Total: new Money(total, currency),
            Remaining: new Money(
                request.Budget.Amount - total,
                currency),
            IsWithinBudget: total <= request.Budget.Amount,
            Lines: lines.AsReadOnly());
    }
}