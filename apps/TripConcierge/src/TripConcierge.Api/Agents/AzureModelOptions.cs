using System.ComponentModel.DataAnnotations;

namespace TripConcierge.Api.Agents;

public sealed class AzureModelOptions
{
    [Required]
    public string Endpoint { get; init; } = "";

    [Required]
    public string ApiKey { get; init; } = "";

    [Required]
    public string ChatDeployment { get; init; } = "";
}