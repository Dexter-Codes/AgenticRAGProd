namespace TripConcierge.Core.Trips;

public static class TripRequestValidator
{
    public static Dictionary<string, string[]> Validate(
        TripRequest? request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request is null)
        {
            errors["request"] = ["A trip request is required."];
            return errors;
        }

        if (request.SelectedDestinationId is <= 0)
        {
            errors[nameof(request.SelectedDestinationId)] =
                ["Selected destination ID must be positive."];
        }

        if (string.IsNullOrWhiteSpace(request.Origin))
            errors[nameof(request.Origin)] = ["Origin is required."];

        if (string.IsNullOrWhiteSpace(request.Destination))
            errors[nameof(request.Destination)] =
                ["Destination is required."];

        if (!string.IsNullOrWhiteSpace(request.Origin) &&
            !string.IsNullOrWhiteSpace(request.Destination) &&
            string.Equals(
                request.Origin.Trim(),
                request.Destination.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            errors[nameof(request.Destination)] =
                ["Origin and destination must differ."];
        }

        if (request.DepartureDate == default)
            errors[nameof(request.DepartureDate)] =
                ["Departure date is required."];

        if (request.ReturnDate == default)
        {
            errors[nameof(request.ReturnDate)] =
                ["Return date is required."];
        }
        else if (request.DepartureDate != default &&
                 request.ReturnDate < request.DepartureDate)
        {
            errors[nameof(request.ReturnDate)] =
                ["Return date cannot precede departure."];
        }

        if (request.TravelerCount <= 0)
            errors[nameof(request.TravelerCount)] =
                ["At least one traveler is required."];

        if (!Enum.IsDefined(request.Style))
            errors[nameof(request.Style)] =
                ["Travel style is invalid."];

        if (request.Budget is null)
        {
            errors[nameof(request.Budget)] = ["Budget is required."];
        }
        else
        {
            if (request.Budget.Amount <= 0)
                errors["Budget.Amount"] =
                    ["Budget must be greater than zero."];

            if (!string.Equals(
                    request.Budget.Currency?.Trim(),
                    "INR",
                    StringComparison.OrdinalIgnoreCase))
            {
                errors["Budget.Currency"] =
                    ["Only INR is supported in this milestone."];
            }
        }

        return errors;
    }
}