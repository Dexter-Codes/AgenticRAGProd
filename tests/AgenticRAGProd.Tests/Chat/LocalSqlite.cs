using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Infrastructure.Persistence;

namespace AgenticRAGProd.Tests.Chat;

// Every harness uses a real, isolated SQLite file. Only failure timing is simulated.
internal sealed class LocalSqlite : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rag-sqlite-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(_directory, "chat.db");
    public FakeClock Clock { get; } = new();
    public bool FailNextMessageCreate { get; set; }
    public bool LoseNextMessageResponse { get; set; }
    public SqliteConversationStore RealStore() => new(Options.Create(new SqliteOptions { DatabasePath = DatabasePath }));
    public IConversationStore Store() => new FaultingStore(this, RealStore());

    public IReadOnlyList<JsonElement> Messages
    {
        get
        {
            if (!File.Exists(DatabasePath)) return [];
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, conversationId, email, query, answer, createdAtUtc FROM chat_messages ORDER BY createdAtUtc, id";
            using var reader = command.ExecuteReader();
            var items = new List<JsonElement>();
            while (reader.Read())
                items.Add(JsonSerializer.SerializeToElement(Enumerable.Range(0, reader.FieldCount)
                    .ToDictionary(reader.GetName, reader.GetString)));
            return items;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FaultingStore(LocalSqlite db, IConversationStore store) : IConversationStore
    {
        public Task<ChatHistoryPage> GetRecentMessagesAsync(Guid id, int limit, CancellationToken token)
            => store.GetRecentMessagesAsync(id, limit, token);
        public async Task SaveAsync(ChatMessage message, CancellationToken token)
        {
            if (db.FailNextMessageCreate)
            {
                db.FailNextMessageCreate = false;
                throw new ChatStorageException("Synthetic storage write failure.", new IOException());
            }
            await store.SaveAsync(message, token);
            if (db.LoseNextMessageResponse)
            {
                db.LoseNextMessageResponse = false;
                throw new HttpRequestException("Synthetic lost response after commit.");
            }
        }
    }
}

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
