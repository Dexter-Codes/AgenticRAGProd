using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TripConcierge.Api.Travel;
using TripConcierge.Api.Workflows;
using TripConcierge.Core.Common;
using TripConcierge.Core.Trips;
using Xunit;

namespace TripConcierge.Tests;

public sealed class DevelopmentUiEndpointTests
{
    [Fact]
    public async Task ReadReturnsSavedSelectionAndCheckpointWithoutChangingVersionOrLease()
    {
        await using var host = await Host.Start();
        var id = Guid.NewGuid();
        var request = new TripRequest("Bengaluru", "Hanoi", new(2026, 12, 1), new(2026, 12, 3), 2,
            TravelStyle.Backpacking, new Money(60000, "INR"));
        var store = host.App.Services.GetRequiredService<TripRunStore>();
        store.CreateIfMissing(id, request);
        Assert.True(store.TryClaim(id, 0, 1581130));
        Assert.True(store.TrySaveCheckpoint(id, 1, new TripRunCheckpoint(LastCompletedStage: "destination_lookup")));
        var response = await host.Client.GetAsync($"/api/trip-runs/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1581130, body.GetProperty("selectedDestinationId").GetInt32());
        Assert.Equal("destination_lookup", body.GetProperty("checkpoint").GetProperty("lastCompletedStage").GetString());
        Assert.Equal("Hanoi", body.GetProperty("request").GetProperty("destination").GetString());
        Assert.Equal(1, store.Find(id)!.Version);
        Assert.False(store.TryClaim(id, 1, 1581130));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/trip-runs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task BridgesPreserveMultipartAndHttpErrorsAndUseOnlyConfiguredUpstream()
    {
        await using var host = await Host.Start();
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4")), "file", "guide.pdf");
        var response = await host.Client.PostAsync("/api/documents", upload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("{\"error\":\"upstream validation\"}", await response.Content.ReadAsStringAsync());
        Assert.StartsWith("multipart/form-data; boundary=", host.Upstream.ContentType);
        Assert.Contains("%PDF-1.4", host.Upstream.Body);
        Assert.Contains("guide.pdf", host.Upstream.Body);
        Assert.Equal("configured-rag.test", host.Upstream.Uri!.Host);
        await host.Client.PostAsync($"/api/documents/{Guid.NewGuid()}/index?documentName=Guide%20%26%20Map", null);
        Assert.Contains("documentName=Guide%20%26%20Map", host.Upstream.Uri!.OriginalString);
        await host.Client.GetAsync("/api/local/capabilities");
        Assert.Equal("/api/local/capabilities", host.Upstream.Uri!.AbsolutePath);
        var previous = host.Upstream.Calls;
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/proxy?url=https://example.com")).StatusCode);
        Assert.Equal(previous, host.Upstream.Calls);
    }

    [Fact]
    public async Task InvalidMcpSearchFailsBeforeAnyUpstreamCall()
    {
        await using var host = await Host.Start();
        var response = await host.Client.PostAsJsonAsync("/api/mcp/search", new { destinationId = 0, query = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Upstream.Calls);
    }

    [Fact]
    public async Task ReadAndBridgeRoutesAreAbsentOutsideDevelopment()
    {
        await using var host = await Host.Start("Production");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/trip-runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/local/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/mcp/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/api/chat", new { query = "hello" })).StatusCode);
        Assert.Equal(0, host.Upstream.Calls);
    }

    private sealed class Upstream : HttpMessageHandler
    {
        public Uri? Uri; public string? ContentType; public string? Body; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri; ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            return new(HttpStatusCode.UnprocessableEntity)
            { Content = new StringContent("{\"error\":\"upstream validation\"}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App; public required HttpClient Client; public required Upstream Upstream;
        public required string DirectoryPath;
        public static async Task<Host> Start(string environment = "Development")
        {
            var dir = Path.Combine(Path.GetTempPath(), "trip-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = dir, EnvironmentName = environment });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var upstream = new Upstream();
            builder.Services.AddSingleton(new TravelKnowledgeClient(new HttpClient(upstream) { BaseAddress = new("http://configured-rag.test/") }, NullLogger<TravelKnowledgeClient>.Instance));
            builder.Services.AddSingleton<TripRunStore>();
            var app = builder.Build(); app.MapDevelopmentUiEndpoints(); await app.StartAsync();
            return new Host { App = app, Client = new HttpClient { BaseAddress = new(app.Urls.Single()) }, Upstream = upstream, DirectoryPath = dir };
        }
        public async ValueTask DisposeAsync()
        { Client.Dispose(); await App.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
}
