using System.Text.Json;
using Microsoft.Data.Sqlite;
using TripConcierge.Api.Agents;
using TripConcierge.Core.Trips;

namespace TripConcierge.Api.Workflows;

public sealed record SavedTripRun(
    TripRequest Request,
    int? SelectedDestinationId,
    TripDraft? Draft,
    long Version);

public sealed class TripRunStore
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly string _connectionString;
    private readonly ILogger<TripRunStore> _logger;

    public TripRunStore(
        IWebHostEnvironment environment,
        ILogger<TripRunStore> logger)
    {
        _logger = logger;
        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "trip-runs.db"),
            DefaultTimeout = 5
        }.ToString();

        Execute("""
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS trip_runs (
                id TEXT PRIMARY KEY,
                request TEXT NOT NULL,
                selected_id INTEGER,
                draft TEXT,
                version INTEGER NOT NULL DEFAULT 0,
                lease_until INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS trip_run_checkpoints (
                run_id TEXT PRIMARY KEY,
                payload TEXT NOT NULL
            );
            """);
    }

    public void CreateIfMissing(Guid runId, TripRequest request)
    {
        Execute("""
            INSERT INTO trip_runs (id, request)
            VALUES ($id, $request)
            ON CONFLICT(id) DO NOTHING;
            """,
            ("$id", runId.ToString("D")),
            ("$request", JsonSerializer.Serialize(request, Json)));
    }

    public SavedTripRun? Find(Guid runId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request, selected_id, draft, version
            FROM trip_runs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", runId.ToString("D"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SavedTripRun(
            JsonSerializer.Deserialize<TripRequest>(reader.GetString(0), Json)
                ?? throw new InvalidOperationException("Invalid saved trip request."),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2)
                ? null
                : JsonSerializer.Deserialize<TripDraft>(reader.GetString(2), Json)
                    ?? throw new InvalidOperationException("Invalid saved trip draft."),
            reader.GetInt64(3));
    }

    public bool TryClaim(Guid runId, long version, int? selectedId) =>
        Execute("""
            UPDATE trip_runs
            SET selected_id = COALESCE(selected_id, $selected),
                version = version + 1,
                lease_until = unixepoch() + 180
            WHERE id = $id
              AND version = $version
              AND lease_until <= unixepoch();
            """,
            ("$id", runId.ToString("D")),
            ("$version", version),
            ("$selected", selectedId)) == 1;

    public bool TrySave(Guid runId, long claimedVersion, TripDraft draft) =>
        Execute("""
            UPDATE trip_runs
            SET draft = $draft,
                version = version + 1,
                lease_until = 0
            WHERE id = $id AND version = $version;
            """,
            ("$id", runId.ToString("D")),
            ("$version", claimedVersion),
            ("$draft", JsonSerializer.Serialize(draft, Json))) == 1;

    public void Release(Guid runId, long claimedVersion)
    {
        try
        {
            Execute("""
                UPDATE trip_runs
                SET lease_until = 0
                WHERE id = $id AND version = $version;
                """,
                ("$id", runId.ToString("D")),
                ("$version", claimedVersion));
        }
        catch (SqliteException exception)
        {
            _logger.LogWarning(
                "Could not release trip run {RunId}. SQLite error {ErrorCode}",
                runId, exception.SqliteErrorCode);
        }
    }

    private int Execute(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    public TripRunCheckpoint LoadCheckpoint(Guid runId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload
            FROM trip_run_checkpoints
            WHERE run_id = $id;
            """;

        command.Parameters.AddWithValue("$id", runId.ToString("D"));

        if (command.ExecuteScalar() is not string payload)
        {
            return new TripRunCheckpoint();
        }

        return JsonSerializer.Deserialize<TripRunCheckpoint>(payload, Json)
            ?? throw new InvalidOperationException(
                "Invalid saved workflow checkpoint.");
    }

    public bool TrySaveCheckpoint(
        Guid runId,
        long claimedVersion,
        TripRunCheckpoint checkpoint) =>
        Execute("""
            INSERT INTO trip_run_checkpoints (run_id, payload)
            SELECT id, $payload
            FROM trip_runs
            WHERE id = $id AND version = $version
            ON CONFLICT(run_id) DO UPDATE SET payload = excluded.payload;
            """,
            ("$id", runId.ToString("D")),
            ("$version", claimedVersion),
            ("$payload", JsonSerializer.Serialize(checkpoint, Json))) == 1;
    }