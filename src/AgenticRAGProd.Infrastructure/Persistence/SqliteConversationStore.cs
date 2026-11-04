using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.Persistence;

internal sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly object _initializationLock = new();
    private bool _initialized;

    public SqliteConversationStore(IOptions<SqliteOptions> options, string? basePath = null)
    {
        _databasePath = Path.GetFullPath(options.Value.DatabasePath, basePath ?? Directory.GetCurrentDirectory());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = options.Value.CommandTimeoutSeconds,
            Pooling = false
        }.ToString();
    }

    // Microsoft.Data.Sqlite executes synchronously, even through its async APIs.
    // Keep the application Task contract while using short, bounded SQL operations.
    public Task SaveAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conversation = ConversationKey(message.ConversationId);
        if (string.IsNullOrWhiteSpace(message.Id) || message.Id.Length > 1023
            || message.Id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ChatInputException("Invalid message identifier.");
        if (string.IsNullOrWhiteSpace(message.Email) || string.IsNullOrWhiteSpace(message.Query)
            || string.IsNullOrWhiteSpace(message.Answer))
            throw new InvalidOperationException("Invalid completed message.");

        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO chat_messages (conversationId, id, email, query, answer, createdAtUtc)
                VALUES ($conversation, $id, $email, $query, $answer, $created);
                """;
            command.Parameters.AddWithValue("$conversation", conversation);
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$email", message.Email);
            command.Parameters.AddWithValue("$query", message.Query);
            command.Parameters.AddWithValue("$answer", message.Answer);
            command.Parameters.AddWithValue("$created", message.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cancellationToken.ThrowIfCancellationRequested();
            command.ExecuteNonQuery(); // INSERT only: a collision never replaces an existing exchange.
            return Task.CompletedTask;
        });
    }

    public Task<ChatHistoryPage> GetRecentMessagesAsync(Guid conversationId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ChatInputException("History limit must be between 1 and 100.");
        var conversation = ConversationKey(conversationId);
        return WithDatabase(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, email, query, answer, createdAtUtc FROM chat_messages
                WHERE conversationId = $conversation
                ORDER BY createdAtUtc DESC, id DESC LIMIT $count;
                """;
            command.Parameters.AddWithValue("$conversation", conversation);
            command.Parameters.AddWithValue("$count", limit + 1);
            using var reader = command.ExecuteReader();
            var messages = new List<ChatMessage>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                messages.Add(new ChatMessage(reader.GetString(0), conversationId, reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture)));
            }
            return Task.FromResult(new ChatHistoryPage(messages.Take(limit).Reverse().ToArray(), messages.Count > limit));
        });
    }

    private T WithDatabase<T>(Func<SqliteConnection, T> operation)
    {
        try
        {
            EnsureDatabase();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return operation(connection);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new ChatStorageException(
                "Chat storage is unavailable. Verify Sqlite:DatabasePath points to a writable local database file.", error);
        }
    }

    private void EnsureDatabase()
    {
        lock (_initializationLock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS chat_messages (
                    conversationId TEXT NOT NULL,
                    id TEXT NOT NULL,
                    email TEXT NOT NULL,
                    query TEXT NOT NULL,
                    answer TEXT NOT NULL,
                    createdAtUtc TEXT NOT NULL,
                    PRIMARY KEY (conversationId, id)
                );
                CREATE INDEX IF NOT EXISTS ix_chat_messages_history
                    ON chat_messages (conversationId, createdAtUtc DESC, id DESC);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private static string ConversationKey(Guid id) => id == Guid.Empty
        ? throw new ChatInputException("Conversation ID cannot be empty.") : id.ToString("N");
}
