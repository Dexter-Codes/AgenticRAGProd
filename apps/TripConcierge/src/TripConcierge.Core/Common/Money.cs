namespace TripConcierge.Core.Common;

public sealed record Money(
    decimal Amount,
    string Currency);