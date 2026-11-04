using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using AgenticRAGProd.Api;
using AgenticRAGProd.Application;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class StaticUiTests
{
    [Theory]
    [InlineData("Development", "/", true, 200)]
    [InlineData("Development", "/index.html", true, 200)]
    [InlineData("Development", "/", false, 200)]
    [InlineData("Production", "/", false, 404)]
    [InlineData("Production", "/index.html", false, 404)]
    public async Task WorkbenchIsServedOnlyInDevelopment(string environment, string path, bool chat, int expectedStatus)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = ApiRoot(),
            WebRootPath = "wwwroot"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:LocalSingleUserMode"] = chat.ToString() });
        builder.Services.AddLogging();
        builder.Services.AddRouting();
        builder.Services.AddProblemDetails();
        builder.Services.AddApiDocumentation();
        builder.Services.AddApplication();
        var server = new Mock<IServer>();
        server.SetupGet(value => value.Features).Returns(new FeatureCollection());
        builder.Services.AddSingleton(server.Object);
        await using var app = global::Program.CreateApp(builder);
        var pipeline = ((IApplicationBuilder)app).Build();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await pipeline(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        if (expectedStatus == 200)
        {
            Assert.StartsWith("text/html", context.Response.ContentType);
            Assert.Contains("<title>AgenticRAGProd", body);
            Assert.Contains("id=\"upload-form\"", body);
            Assert.Contains("id=\"index-form\"", body);
            Assert.Contains("id=\"chat-form\"", body);
        }
        else Assert.DoesNotContain("API Workbench", body);
    }

    private static string ApiRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var root = Path.Combine(directory.FullName, "src", "AgenticRAGProd.Api");
            if (File.Exists(Path.Combine(root, "wwwroot", "index.html"))) return root;
        }
        throw new DirectoryNotFoundException("Could not locate the API workbench source.");
    }
}
