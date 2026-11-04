using TripConcierge.Core.Common;

namespace TripConcierge.Core.Trips;

public sealed record TripRequest(
    string Origin,
    string Destination,
    DateOnly DepartureDate,
    DateOnly ReturnDate,
    int TravelerCount,
    TravelStyle Style,
    Money Budget,
    int? SelectedDestinationId = null);