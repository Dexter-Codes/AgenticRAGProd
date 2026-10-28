using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Moq;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Persistence;
using Xunit;
using Message = AgenticRAGProd.Infrastructure.Persistence.CosmosConversationStore.MessageItem;

namespace AgenticRAGProd.Tests.Chat;

// Execute the production store against a strict SDK mock. Only message
// creates and bounded history queries are supported. Unexpected operations,
// state records, batches, upserts, replacements and deletes fail immediately.
internal sealed class LocalCosmos
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Partition, string Id), JsonElement> _messages = [];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public Mock<Container> MessagesContainer { get; } = new(MockBehavior.Strict);
    public FakeClock Clock { get; } = new();
    public bool FailNextMessageCreate { get; set; }
    public bool LoseNextMessageResponse { get; set; }
    public Func<Task>? BeforeNextMessageCreate { get; set; }
    public List<string> Writes { get; } = [];
    public List<string> Queries { get; } = [];
    public int QueryPagesRead { get; private set; }
    public IReadOnlyList<JsonElement> Messages { get { lock (_gate) return _messages.Values.ToArray(); } }
    public CosmosConversationStore Store() => new(MessagesContainer.Object);

    public LocalCosmos()
    {
        SetupMessageCreate();
        MessagesContainer.Setup(c => c.GetItemQueryIterator<Message>(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<QueryRequestOptions>()))
            .Returns((QueryDefinition query, string _, QueryRequestOptions options) => Query(query, options));
    }

    private void SetupMessageCreate()
    {
        MessagesContainer.Setup(c => c.CreateItemAsync(It.IsAny<Message>(), It.IsAny<PartitionKey?>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (Message item, PartitionKey? partition, ItemRequestOptions _, CancellationToken token) =>
            {
                var hook = BeforeNextMessageCreate;
                BeforeNextMessageCreate = null;
                if (hook is not null) await hook();
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    var json = JsonSerializer.SerializeToElement(item, JsonOptions);
                    Assert.Equal(new PartitionKey(item.ConversationId), partition!.Value);
                    var key = (partition.Value.ToString(), item.Id);
                    if (_messages.ContainsKey(key)) throw Error(HttpStatusCode.Conflict);
                    if (FailNextMessageCreate)
                    {
                        FailNextMessageCreate = false;
                        throw Error(HttpStatusCode.ServiceUnavailable);
                    }
                    _messages.Add(key, json);
                    Writes.Add("messages:" + item.Id);
                    if (LoseNextMessageResponse)
                    {
                        LoseNextMessageResponse = false;
                        throw new HttpRequestException("Synthetic lost message response after commit.");
                    }
                    return Response(item);
                }
            });
    }

    private FeedIterator<Message> Query(QueryDefinition query, QueryRequestOptions options)
    {
        Assert.NotNull(options.PartitionKey);
        var partition = options.PartitionKey ?? throw new InvalidOperationException("Missing partition.");
        Assert.Contains("ORDER BY c.createdAtUtc DESC, c.id DESC", query.QueryText);
        Assert.Contains("TOP @count", query.QueryText);
        Assert.DoesNotContain("c.type", query.QueryText);
        Assert.DoesNotContain("c.status", query.QueryText);
        Queries.Add(query.QueryText);
        var parameters = query.GetQueryParameters().ToDictionary(p => p.Name, p => p.Value);
        var count = Convert.ToInt32(parameters["@count"]);
        Assert.InRange(count, 2, 101);
        Assert.Equal(count, options.MaxItemCount);
        List<Message> messages;
        lock (_gate)
            messages = _messages.Where(pair => pair.Key.Partition == partition.ToString())
                .Select(pair => pair.Value.Deserialize<Message>(JsonOptions)!)
                .OrderByDescending(message => message.CreatedAtUtc, StringComparer.Ordinal)
                .ThenByDescending(message => message.Id, StringComparer.Ordinal).Take(count).ToList();
        // An empty page followed by single-row pages detects early termination
        // based on SDK page size instead of HasMoreResults and the row bound.
        var pages = new Queue<Message[]>(new[] { Array.Empty<Message>() }.Concat(messages.Select(message => new[] { message })));
        var iterator = new Mock<FeedIterator<Message>>();
        iterator.SetupGet(i => i.HasMoreResults).Returns(() => pages.Count > 0);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            QueryPagesRead++;
            var values = pages.Dequeue();
            var response = new Mock<FeedResponse<Message>>();
            response.Setup(r => r.GetEnumerator()).Returns(() => ((IEnumerable<Message>)values).GetEnumerator());
            return Task.FromResult(response.Object);
        });
        return iterator.Object;
    }

    private static ItemResponse<T> Response<T>(T item)
    {
        var response = new Mock<ItemResponse<T>>();
        response.SetupGet(r => r.Resource).Returns(item);
        return response.Object;
    }
    private static CosmosException Error(HttpStatusCode status) => new("Synthetic Cosmos response", status, 0, "local", 0);
}

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
