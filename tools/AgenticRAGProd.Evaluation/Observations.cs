using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgenticRAGProd.Evaluation;

public sealed record RequestObservation(string Question, string? ConversationId, bool Attempted,
    int? StatusCode, bool? ValidJson, JsonElement? Actual, string? RawBody, string? Error, double? ClientElapsedMs);

public sealed record Observation(int SchemaVersion, string RunId, string CaseId, string Question,
    DateTimeOffset? RecordedAtUtc, string DatasetVersion, string DatasetHash, string AnswerKeyHash,
    string RubricHash, Dictionary<string, string> Provenance, RequestObservation[] SetupTurns,
    bool SetupCaptured, RequestObservation Target, string? BlockedReason, string SourceFormat = "v1");

public static class ObservationReader
{
    public static Observation[] Read(string path)
    {
        path = Path.GetFullPath(path);
        var result = new List<Observation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    EvaluationJson.NoDuplicateProperties(root);
                    Observation observation;
                    if (root.TryGetProperty("schemaVersion", out _))
                    {
                        if (EvaluationJson.Integer(root, "schemaVersion") != 1) throw new JsonException("Unsupported observation schema.");
                        observation = root.Deserialize<Observation>(EvaluationJson.Options) ?? throw new JsonException();
                    }
                    else observation = Legacy(root);
                    if (string.IsNullOrWhiteSpace(observation.CaseId) || !EvaluationDataset.ValidQuestion(observation.Question)
                        || !EvaluationDataset.ValidQuestion(observation.Target.Question)
                        || observation.SetupTurns.Any(turn => turn is null || !EvaluationDataset.ValidQuestion(turn.Question)))
                        throw new JsonException("Missing observation identity.");
                    if (!ids.Add(observation.CaseId))
                        throw new EvaluationInputException($"Duplicate observation for {observation.CaseId}; use one capture run per input. [{path}:{lineNumber}]");
                    result.Add(observation);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                { throw new EvaluationInputException($"Invalid observation JSON/schema at {path}:{lineNumber}: {error.GetType().Name}."); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new EvaluationInputException($"Cannot read observation input at {path}: {error.GetType().Name}."); }
        return result.ToArray();
    }

    private static Observation Legacy(JsonElement root)
    {
        var caseId = EvaluationJson.Text(root, "caseId") ?? throw new JsonException();
        var question = EvaluationJson.Text(root, "question") ?? throw new JsonException();
        JsonElement? actual = root.TryGetProperty("actual", out var field) && field.ValueKind != JsonValueKind.Null ? field.Clone() : null;
        double? latency = root.TryGetProperty("clientElapsedMs", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number
            && elapsed.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;
        DateTimeOffset? time = DateTimeOffset.TryParse(EvaluationJson.Text(root, "recordedAtUtc"), out var parsed) ? parsed : null;
        // Historical expectedAnswerKind/expectedFacts fields are deliberately ignored.
        return new(1, EvaluationJson.Text(root, "runId") ?? "unknown", caseId, question, time,
            "unknown", "unknown", "unknown", "unknown", Provenance.Unknown(), [], false,
            new(question, null, true, EvaluationJson.Integer(root, "statusCode"), actual.HasValue ? true : null,
                actual, null, EvaluationJson.Text(root, "error"), latency), null, "legacy");
    }
}

public static class Provenance
{
    public static readonly string[] Names = ["answerServiceCommit", "answerDeployment", "answerModel",
        "embeddingDeployment", "indexName", "corpusVersion", "promptVersion", "configurationVersion"];
    public static Dictionary<string, string> Unknown() => Names.ToDictionary(name => name, _ => "unknown", StringComparer.Ordinal);

    public static Dictionary<string, string> Read(string? path)
    {
        var result = Unknown();
        if (path is null) return result;
        var configured = EvaluationJson.Read<Dictionary<string, string>>(path);
        foreach (var item in configured)
        {
            if (!result.ContainsKey(item.Key) || string.IsNullOrWhiteSpace(item.Value))
                throw new EvaluationInputException($"Unsupported or empty non-secret provenance field '{item.Key}'. [{Path.GetFullPath(path)}]");
            result[item.Key] = item.Value;
        }
        return result;
    }

    public static string ClientCommit(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "-C", directory, "rev-parse", "--verify", "HEAD" },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            if (process is null || !process.WaitForExit(3000)) { process?.Kill(); return "unknown"; }
            var value = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit) ? value : "unknown";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return "unknown"; }
    }
}

public sealed class CaptureRunner(HttpClient client)
{
    public async Task<Observation> CaptureAsync(EvaluationCase item, RunMetadata run, CancellationToken cancellationToken = default)
    {
        var setup = new List<RequestObservation>();
        string? conversationId = null;
        string? blocked = null;
        foreach (var question in item.SetupTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = await SendAsync(question, conversationId, cancellationToken);
            setup.Add(turn);
            if (!SuccessfulSetup(turn)) { blocked = $"Setup turn {setup.Count} failed transport or response contract; target was not requested."; break; }
            conversationId = EvaluationJson.Text(turn.Actual!.Value, "conversationId");
        }
        var target = blocked is null
            ? await SendAsync(item.Question, conversationId, cancellationToken)
            : new RequestObservation(item.Question, conversationId, false, null, null, null, null, null, null);
        return new(1, run.RunId, item.Id, item.Question, DateTimeOffset.UtcNow,
            run.DatasetVersion, run.DatasetHash, run.AnswerKeyHash, run.RubricHash,
            run.AnswerServiceProvenance, setup.ToArray(), true, target, blocked);
    }

    public static bool SuccessfulSetup(RequestObservation request) => Scoring.Transport(request).Status == Assessment.Pass
        && Scoring.ResponseContract(request).Status == Assessment.Pass
        && Guid.TryParse(EvaluationJson.Text(request.Actual!.Value, "conversationId"), out var id) && id != Guid.Empty;

    private async Task<RequestObservation> SendAsync(string question, string? conversationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        int? status = null;
        string? raw = null, error = null;
        JsonElement? actual = null;
        bool? validJson = null;
        try
        {
            // The answer boundary has exactly these two fields. References never enter it.
            using var response = await client.PostAsJsonAsync("api/chat", new { conversationId, query = question }, token);
            status = (int)response.StatusCode;
            raw = await response.Content.ReadAsStringAsync(token);
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                EvaluationJson.NoDuplicateProperties(parsed.RootElement);
                actual = parsed.RootElement.Clone(); validJson = true;
            }
            catch (JsonException) { validJson = false; }
            if (!response.IsSuccessStatusCode) error = $"HTTP {status}";
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            error = exception.GetType().Name;
        }
        return new(question, conversationId, true, status, validJson, actual, raw, error,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
