using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using AgenticRAGProd.Api;
using AgenticRAGProd.Application;
using AgenticRAGProd.Application.Abstractions;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class SwaggerTests
{
    [Theory]
    [InlineData("Development", true, 200)]
    [InlineData("Development", false, 200)]
    [InlineData("Production", false, 404)]
    public async Task SwaggerListsUploadAndIndexingAndRespectsEnvironmentAndChatGate(string environment, bool chat, int expectedStatus)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:LocalSingleUserMode"] = chat.ToString() });
        builder.Services.AddLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddRouting();
        var server = new Mock<IServer>();
        server.SetupGet(s => s.Features).Returns(new FeatureCollection());
        builder.Services.AddSingleton(server.Object);
        builder.Services.AddApplication();
        builder.Services.AddSingleton(new Mock<IConversationStore>().Object);
        builder.Services.AddApiDocumentation();
        WebApplication? application = null;
        builder.Services.AddSingleton<EndpointDataSource>(_ => new CompositeEndpointDataSource(((IEndpointRouteBuilder)application!).DataSources));
        await using var app = application = global::Program.CreateApp(builder);
        var pipeline = ((IApplicationBuilder)app).Build();
        var ui = await Send(app, pipeline, "/swagger/index.html");
        Assert.Equal(expectedStatus, ui.Status);
        var document = await Send(app, pipeline, "/swagger/v1/swagger.json");
        Assert.Equal(expectedStatus, document.Status);
        if (expectedStatus == 404) return;
        Assert.Contains("Swagger UI", ui.Body);
        using var json = JsonDocument.Parse(document.Body);
        var paths = json.RootElement.GetProperty("paths");
        Assert.Equal(chat ? 3 : 2, paths.EnumerateObject().Count());
        var upload = paths.GetProperty("/api/documents").GetProperty("post");
        Assert.True(upload.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        var indexing = paths.GetProperty("/api/documents/{documentId}/index").GetProperty("post");
        var indexParameters = indexing.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(indexParameters, parameter => parameter.GetProperty("name").GetString() == "documentId"
            && parameter.GetProperty("in").GetString() == "path" && parameter.GetProperty("required").GetBoolean());
        Assert.Contains(indexParameters, parameter => parameter.GetProperty("name").GetString() == "documentName"
            && parameter.GetProperty("in").GetString() == "query" && parameter.GetProperty("required").GetBoolean());
        if (!chat)
        {
            Assert.False(paths.TryGetProperty("/api/chat", out _));
            return;
        }
        var send = paths.GetProperty("/api/chat").GetProperty("post");
        Assert.False(send.TryGetProperty("parameters", out var parameters) && parameters.GetArrayLength() > 0);
        Assert.True(send.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
    }

    private static async Task<(int Status, string Body)> Send(WebApplication app, RequestDelegate pipeline, string path)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        await pipeline(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
