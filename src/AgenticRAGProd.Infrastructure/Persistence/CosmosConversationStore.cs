using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.Persistence;

internal sealed class CosmosConversationStore : IConversationStore
{
    private readonly Container _messages;

    public CosmosConversationStore(CosmosClient client, IOptions<CosmosDbOptions> options)
        : this(client.GetContainer(options.Value.DatabaseName, options.Value.ContainerName)) { }

    internal CosmosConversationStore(Container messages) => _messages = messages;

    public async Task SaveAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        var partition = Partition(message.ConversationId);
        ValidateId(message.Id);
        var item = new MessageItem
        {
            Id = message.Id, ConversationId = partition, Email = message.Email, Query = message.Query,
            Answer = message.Answer, CreatedAtUtc = Timestamp(message.CreatedAtUtc)
        };
        ValidateMessage(item, partition);

        try
        {
            await _messages.CreateItemAsync(item, new PartitionKey(partition), cancellationToken: cancellationToken);
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ChatStorageException(
                "Chat storage was not found. Verify CosmosDb:DatabaseName and CosmosDb:ContainerName, " +
                "and ensure that container exists with partition key /conversationId.", error);
        }
    }

    public async Task<ChatHistoryPage> GetRecentMessagesAsync(Guid conversationId, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ChatInputException("History limit must be between 1 and 100.");
        var partition = Partition(conversationId);
        var query = new QueryDefinition("SELECT TOP @count * FROM c ORDER BY c.createdAtUtc DESC, c.id DESC")
            .WithParameter("@count", limit + 1);
        using var iterator = _messages.GetItemQueryIterator<MessageItem>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(partition), MaxItemCount = limit + 1
        });
        var items = new List<MessageItem>();
        // Empty or short SDK pages do not imply that the bounded query ended.
        while (iterator.HasMoreResults && items.Count < limit + 1)
        {
            FeedResponse<MessageItem> page;
            try
            {
                page = await iterator.ReadNextAsync(cancellationToken);
            }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
                && error.Message.Contains("composite index", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatStorageException(
                    "Chat history requires a composite index on /createdAtUtc DESC and /id DESC in the configured " +
                    "CosmosDb:ContainerName container. Add it to the existing indexing policy and wait for indexing to finish.", error);
            }
            foreach (var item in page)
            {
                ValidateMessage(item, partition);
                items.Add(item);
                if (items.Count == limit + 1) break;
            }
        }
        var selected = items.OrderByDescending(item => item.CreatedAtUtc, StringComparer.Ordinal)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal).Take(limit).ToArray();
        return new ChatHistoryPage(selected.Reverse().Select(ToMessage).ToArray(), items.Count > limit);
    }

    private static void ValidateMessage(MessageItem item, string partition)
    {
        if (item.ConversationId != partition || string.IsNullOrWhiteSpace(item.Id)
            || string.IsNullOrWhiteSpace(item.Email) || string.IsNullOrWhiteSpace(item.Query)
            || string.IsNullOrWhiteSpace(item.Answer) || !IsTimestamp(item.CreatedAtUtc))
            throw new InvalidOperationException("Invalid stored completed message. Review the legacy-data migration instructions.");
    }

    private static ChatMessage ToMessage(MessageItem item) => new(item.Id, Guid.ParseExact(item.ConversationId, "N"),
        item.Email, item.Query, item.Answer, ParseTimestamp(item.CreatedAtUtc));

    private static string Partition(Guid id) => id == Guid.Empty
        ? throw new ChatInputException("Conversation ID cannot be empty.") : id.ToString("N");

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1023 || id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ChatInputException("Invalid message identifier.");
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
    private static bool IsTimestamp(string? value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero && Timestamp(parsed) == value;

    internal sealed record MessageItem
    {
        public string Id { get; init; } = "";
        public string ConversationId { get; init; } = "";
        public string Email { get; init; } = "";
        public string Query { get; init; } = "";
        public string Answer { get; init; } = "";
        public string CreatedAtUtc { get; init; } = "";
    }
}
