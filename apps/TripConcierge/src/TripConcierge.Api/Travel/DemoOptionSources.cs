using TripConcierge.Core.Budgeting;
using TripConcierge.Core.Common;

namespace TripConcierge.Api.Travel;

public sealed record TripOptionCatalog(
    IReadOnlyList<TravelOption> Transport,
    IReadOnlyList<TravelOption> Lodging,
    IReadOnlyList<TravelOption> Food);

public static class DemoOptionSources
{
    public static IReadOnlyList<TravelOption> GetTransportOptions() =>
    [
        Offer("transport-standard", 18_000m, CostBasis.PerTraveler),
        Offer("transport-budget", 15_000m, CostBasis.PerTraveler)
    ];

    public static IReadOnlyList<TravelOption> GetLodgingOptions() =>
    [
        Offer("lodging-standard", 1_200m, CostBasis.PerTravelerPerNight),
        Offer("lodging-budget", 800m, CostBasis.PerTravelerPerNight)
    ];

    public static IReadOnlyList<TravelOption> GetFoodOptions() =>
    [
        Offer("food-standard", 600m, CostBasis.PerTravelerPerDay),
        Offer("food-budget", 400m, CostBasis.PerTravelerPerDay)
    ];

    private static TravelOption Offer(
        string id,
        decimal price,
        CostBasis basis) =>
        new(
            id,
            $"Fictional {id}",
            new Money(price, "INR"),
            basis,
            "demo-fixture-v1");
}