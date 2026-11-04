using System.Text.Json;
using System.Text.Json.Serialization;
using TripConcierge.Core.Trips;
using TripConcierge.Core.Budgeting;
using TripConcierge.Core.Common;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using TripConcierge.Api.Agents;
using TripConcierge.Api.Tools;
using TripConcierge.Api.Travel;
using TripConcierge.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<TravelStyle>(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
});
builder.Services
    .AddOptions<AzureModelOptions>()
    .BindConfiguration("AzureOpenAI")
    .ValidateDataAnnotations()
    .Validate(
        value =>
            Uri.TryCreate(
                value.Endpoint,
                UriKind.Absolute,
                out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps,
        "AzureOpenAI:Endpoint must be an absolute HTTPS URL.")
    .ValidateOnStart();

builder.Services.AddSingleton<AzureOpenAIClient>(provider =>
{
    var settings = provider
        .GetRequiredService<IOptions<AzureModelOptions>>()
        .Value;

    return new AzureOpenAIClient(
        new Uri(settings.Endpoint),
        new AzureKeyCredential(settings.ApiKey));
});

builder.Services.AddScoped<TripCoordinator>();
builder.Services.AddScoped<ItineraryPlanner>();

builder.Services.AddHttpClient<DestinationSearchClient>(client =>
{
    client.BaseAddress =
        new Uri("https://geocoding-api.open-meteo.com/");

    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<WeatherClient>(client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<TripPlanningWorkflow>();
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "TripConcierge.Api", status = "ready" }));

app.MapPost("/api/trips/validate", (TripRequest? request) =>
{
    var errors = TripRequestValidator.Validate(request);

    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var validRequest = request!;

    var normalized = validRequest with
    {
        Origin = validRequest.Origin.Trim(),
        Destination = validRequest.Destination.Trim(),
        Budget = validRequest.Budget with
        {
            Currency = validRequest.Budget.Currency
                .Trim()
                .ToUpperInvariant()
        }
    };

    return Results.Ok(new
    {
        status = "validated",
        trip = normalized
    });
});



if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/demo/budget", () =>
    {
        var request = new TripRequest(
            Origin: "Bengaluru",
            Destination: "Hanoi",
            DepartureDate: new DateOnly(2026, 12, 5),
            ReturnDate: new DateOnly(2026, 12, 10),
            TravelerCount: 2,
            Style: TravelStyle.Backpacking,
            Budget: new Money(60_000m, "INR"));

        TravelOption[] options =
        [
            new(
                Id: "demo-transport",
                Description: "Fictional return transport",
                UnitPrice: new Money(18_000m, "INR"),
                Basis: CostBasis.PerTraveler,
                PriceSource: "demo-fixture-v1"),

            new(
                Id: "demo-lodging",
                Description: "Fictional lodging per person",
                UnitPrice: new Money(1_200m, "INR"),
                Basis: CostBasis.PerTravelerPerNight,
                PriceSource: "demo-fixture-v1"),

            new(
                Id: "demo-food",
                Description: "Fictional daily food allowance",
                UnitPrice: new Money(600m, "INR"),
                Basis: CostBasis.PerTravelerPerDay,
                PriceSource: "demo-fixture-v1")
        ];

        return Results.Ok(
            BudgetCalculator.Calculate(request, options));
    });


    app.MapPost("/api/trips/draft", async (
        TripRequest? request,
       TripPlanningWorkflow workflow,
       ILogger<TripPlanningWorkflow> logger,
        CancellationToken cancellationToken) =>
    {
        var errors = TripRequestValidator.Validate(request);

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        try
        {
            return Results.Ok(
            await workflow.RunAsync(request!, cancellationToken));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(
                statusCode: 504,
                title: "Trip planning timed out");
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
                "Trip planning failed. Exception type: {ExceptionType}",
                exception.GetType().FullName);

            return Results.Problem(
                statusCode: 503,
                title: "Trip planning is temporarily unavailable");
        }
    });
}


app.Run();
