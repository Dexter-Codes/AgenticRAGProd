using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using AgenticRAGProd.Api;
using AgenticRAGProd.Application;
using AgenticRAGProd.Application.Abstractions;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatRouteTests
{
    [Theory]
    [InlineData("Development", true, 3)]
    [InlineData("Development", false, 2)]
    [InlineData("Production", false, 2)]
    [InlineData("Staging", false, 2)]
    public async Task ApplicationExposesUploadAndIndexingAndGatesChatOnDevelopmentAndLocalMode(string environment, bool enabled, int count)
    {
        var builder = Builder(environment, enabled);
        await using var app = global::Program.CreateApp(builder);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Equal(count, endpoints.Length);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/documents");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/documents/{documentId:guid}/index");
        Assert.All(endpoints, endpoint =>
            Assert.Equal("POST", Assert.Single(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)));
        if (count == 2)
        {
            Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/chat");
            return;
        }

        var chat = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/chat");
        Assert.Contains("application/json", chat.Metadata.GetMetadata<IAcceptsMetadata>()!.ContentTypes);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void LocalModeOutsideDevelopmentFailsStartup(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => global::Program.CreateApp(Builder(environment, true)));
        Assert.Equal("Local single-user chat is supported only in Development.", error.Message);
    }

    private static WebApplicationBuilder Builder(string environment, bool enabled)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:LocalSingleUserMode"] = enabled.ToString() });
        builder.Services.AddLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddRouting();
        builder.Services.AddApiDocumentation();
        var server = new Mock<IServer>();
        server.SetupGet(s => s.Features).Returns(new FeatureCollection());
        builder.Services.AddSingleton(server.Object);
        builder.Services.AddApplication();
        builder.Services.AddSingleton(new Mock<IConversationStore>().Object);
        return builder;
    }
}
