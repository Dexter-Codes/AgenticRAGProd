using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Infrastructure.Search;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class RetrievalTests
{
    [Fact]
    public async Task CorpusSearchKeepsDocumentProvenanceWithoutFilteringByDocument()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var handler = new LocalHandler(JsonSerializer.Serialize(new
        {
            value = ids.Select((id, i) => new Dictionary<string, object>
            {
                ["@search.score"] = .8, ["id"] = $"c-{i}", ["documentId"] = id.ToString("N"),
                ["documentName"] = $"Guide {i}", ["pageNumber"] = 1, ["content"] = "Evidence", ["hasUnprocessedImages"] = false
            })
        }));
        var retriever = new AzureSearchChunkRetriever(Client(handler), Options.Create(new AzureSearchOptions { EmbeddingDimensions = 3 }));

        var chunks = await retriever.RetrieveCorpusAsync("topic", [1, 0, 0], default);

        Assert.Equal(ids, chunks.Select(chunk => chunk.DocumentId));
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.False(request.RootElement.TryGetProperty("filter", out _));
        Assert.Equal("topic", request.RootElement.GetProperty("search").GetString());
        Assert.Single(request.RootElement.GetProperty("vectorQueries").EnumerateArray());
    }

    [Theory]
    [InlineData(9, 3)]
    [InlineData(17, 3)]
    [InlineData(9, 6)]
    public async Task HybridQueryUsesConfiguredCountsAndPreservesRankedContext(int candidates, int final)
    {
        var id = Guid.NewGuid();
        var context = new ChunkContext("Manual", 2, 4, ["Setup"], ["Uncertain layout"]);
        var handler = new LocalHandler(JsonSerializer.Serialize(new
        {
            value = Enumerable.Range(0, candidates).Select(index => new Dictionary<string, object>
            {
                ["@search.score"] = 1d / (index + 1), ["id"] = $"chunk-{index}",
                ["documentId"] = id.ToString("N"), ["documentName"] = "Manual", ["pageNumber"] = 2,
                ["content"] = context.Prefix() + "Passage", ["hasUnprocessedImages"] = false
            })
        }));
        var settings = Options.Create(new AzureSearchOptions { EmbeddingDimensions = 3, CandidateCount = candidates, FinalContextCount = final });

        var chunks = await new AzureSearchChunkRetriever(Client(handler), settings)
            .RetrieveCorpusAsync("sensor calibration", [1, 0, 0], CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, final).Select(i => $"chunk-{i}"), chunks.Select(chunk => chunk.Id));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(4, chunk.Context!.PageEnd);
            Assert.Contains("Uncertain layout", chunk.Context.Warnings);
        });
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        var root = body.RootElement;
        Assert.Equal("sensor calibration", root.GetProperty("search").GetString());
        Assert.Equal("content", root.GetProperty("searchFields").GetString());
        Assert.Equal("simple", root.GetProperty("queryType").GetString());
        Assert.False(root.TryGetProperty("filter", out _));
        Assert.Equal(candidates, root.GetProperty("top").GetInt32());
        var vector = Assert.Single(root.GetProperty("vectorQueries").EnumerateArray());
        Assert.Equal(candidates, vector.GetProperty("k").GetInt32());
        Assert.Equal("contentVector", vector.GetProperty("fields").GetString());
        Assert.False(root.TryGetProperty("semanticConfiguration", out _));
    }

    [Fact]
    public async Task InvalidVectorsFailBeforeMakingAnySearchRequest()
    {
        var handler = new LocalHandler("{}");
        var retriever = new AzureSearchChunkRetriever(Client(handler), Options.Create(new AzureSearchOptions { EmbeddingDimensions = 3 }));
        await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveCorpusAsync("question", [float.NaN, 0, 0], default));
        await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveCorpusAsync("question", [float.PositiveInfinity, 0, 0], default));
        await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveCorpusAsync("question", [1], default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1001, 1)]
    [InlineData(2, 3)]
    [InlineData(2, 0)]
    public async Task InvalidCountsFailBeforeMakingAnyRequest(int candidates, int final)
    {
        var handler = new LocalHandler("{}");
        var retriever = new AzureSearchChunkRetriever(Client(handler),
            Options.Create(new AzureSearchOptions { CandidateCount = candidates, FinalContextCount = final }));
        await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveCorpusAsync("question", [1], default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReindexCleanupStillFiltersByDocumentAndDeletesOnlyObsoleteChunkIds()
    {
        var id = Guid.NewGuid();
        var handler = new LocalHandler(
            "{\"value\":[{\"id\":\"retained\"},{\"id\":\"obsolete\"}]}",
            "{\"value\":[{\"key\":\"obsolete\",\"status\":true,\"statusCode\":200}]}");
        var writer = new AzureSearchChunkIndexWriter(Client(handler), Options.Create(new AzureSearchOptions()));

        await writer.RemoveObsoleteAsync(id, ["retained"], default);

        Assert.Equal(2, handler.Requests.Count);
        using var search = JsonDocument.Parse(handler.Requests[0]);
        Assert.Equal($"documentId eq '{id:N}'", search.RootElement.GetProperty("filter").GetString());
        using var deletion = JsonDocument.Parse(handler.Requests[1]);
        var item = Assert.Single(deletion.RootElement.GetProperty("value").EnumerateArray());
        Assert.Equal("delete", item.GetProperty("@search.action").GetString());
        Assert.Equal("obsolete", item.GetProperty("id").GetString());
    }

    private static SearchClient Client(LocalHandler handler) => new(new Uri("https://local.invalid"),
        "server-configured-index", new AzureKeyCredential("local-test-key"),
        new SearchClientOptions { Transport = new HttpClientTransport(handler) });

    private sealed class LocalHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
