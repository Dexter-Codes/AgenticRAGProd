using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgenticRAGProd.Evaluation;

public sealed class EvaluationInputException(string message) : Exception(message);

public static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static T Read<T>(string path)
    {
        path = Path.GetFullPath(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            NoDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(Options)
                ?? throw new EvaluationInputException($"Empty JSON document: {path}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new EvaluationInputException($"Cannot read valid {typeof(T).Name} JSON at {path}: {error.GetType().Name}.");
        }
    }

    public static void NoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                NoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) NoDuplicateProperties(item);
    }

    public static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    public static int? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.TryGetInt32Safe(out var result) ? result : null;
    private static bool TryGetInt32Safe(this JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
}

public sealed record EvaluationCase(string Id, string Question, bool Enabled, string? DisabledReason,
    string[] Tags, string[] SetupTurns);
public sealed record CaseDataset(int SchemaVersion, string DatasetVersion,
    Dictionary<string, string[]> Suites, EvaluationCase[] Cases, string? Description = null);
public sealed record SourceReference(string DocumentName, int[] PageNumbers, string[] Sections);
public sealed record AnswerReference(string CaseId, string GroundTruthStatus, string? ExpectedAnswerKind,
    string? ReferenceAnswer, string[] RequiredFacts, string[] ForbiddenClaims,
    SourceReference[] SourceReferences, string[] GradingNotes);
public sealed record AnswerKey(int SchemaVersion, string DatasetVersion, AnswerReference[] Entries,
    Dictionary<string, string>? ReferenceVersions = null, string? Usage = null);
public sealed record Selection(EvaluationCase[] Cases, string Label)
{
    public EvaluationCase[] Enabled => Cases.Where(item => item.Enabled).ToArray();
    public int PlannedRequests => Enabled.Sum(item => item.SetupTurns.Length + 1);
}

public sealed record EvaluationDataset(CaseDataset Cases, AnswerKey Key, string CasesPath,
    string KeyPath, string RubricPath, string CasesHash, string KeyHash, string RubricHash)
{
    public static readonly string[] Kinds = ["answered", "clarification", "insufficient_evidence"];
    public const string RubricVersion = "2.0";

    public static EvaluationDataset Load(string casesPath, string keyPath, string rubricPath)
    {
        casesPath = Path.GetFullPath(casesPath); keyPath = Path.GetFullPath(keyPath); rubricPath = Path.GetFullPath(rubricPath);
        var cases = EvaluationJson.Read<CaseDataset>(casesPath);
        var key = EvaluationJson.Read<AnswerKey>(keyPath);
        void Require([DoesNotReturnIf(false)] bool condition, string message, string? path = null)
        {
            if (!condition) throw new EvaluationInputException($"{message} [{path ?? casesPath}]");
        }
        Require(cases.SchemaVersion == 2 && key.SchemaVersion == 2, "Supported dataset/key schemaVersion is 2.");
        Require(Regex.IsMatch(cases.DatasetVersion, @"^rag-eval-v2\.\d+$")
            && cases.DatasetVersion == key.DatasetVersion, "Incompatible dataset/key versions.");
        Require(cases.Cases.Length > 0, "Dataset contains no cases.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases.Cases)
        {
            if (item is null) throw new EvaluationInputException($"Null case. [{casesPath}]");
            Require(!string.IsNullOrWhiteSpace(item.Id) && ids.Add(item.Id), "Case IDs must be nonempty and unique.");
            Require(ValidQuestion(item.Question), $"Invalid question for {item.Id}.");
            Require(item.SetupTurns.All(ValidQuestion), $"Invalid setup turn for {item.Id}.");
            Require(item.Tags.All(tag => !string.IsNullOrWhiteSpace(tag)), $"Invalid tags for {item.Id}.");
            Require(item.Enabled ? item.DisabledReason is null : !string.IsNullOrWhiteSpace(item.DisabledReason),
                $"Enabled/disabled reason is inconsistent for {item.Id}.");
        }
        foreach (var suite in new[] { "smoke", "full" })
        {
            Require(cases.Suites.TryGetValue(suite, out var members) && members is { Length: > 0 }, $"Missing nonempty {suite} suite.");
            Require(members!.Distinct(StringComparer.Ordinal).Count() == members.Length && members.All(ids.Contains),
                $"Invalid or duplicate IDs in {suite} suite.");
        }
        var joined = new Dictionary<string, AnswerReference>(StringComparer.Ordinal);
        foreach (var entry in key.Entries)
        {
            if (entry is null) throw new EvaluationInputException($"Null key entry. [{keyPath}]");
            Require(ids.Contains(entry.CaseId) && joined.TryAdd(entry.CaseId, entry), "Unknown or duplicate answer-key case ID.", keyPath);
            Require(new[] { "verified_source", "needs_review", "key_only_ocr_unverified" }.Contains(entry.GroundTruthStatus),
                $"Unsupported reference status for {entry.CaseId}.", keyPath);
            Require(entry.ExpectedAnswerKind is null || Kinds.Contains(entry.ExpectedAnswerKind), $"Unsupported lowercase kind for {entry.CaseId}.", keyPath);
            Require(entry.RequiredFacts.Concat(entry.ForbiddenClaims).Concat(entry.GradingNotes).All(value => !string.IsNullOrWhiteSpace(value)),
                $"Blank reference text for {entry.CaseId}.", keyPath);
            foreach (var locator in entry.SourceReferences)
                Require(locator is not null && !string.IsNullOrWhiteSpace(locator.DocumentName) && locator.PageNumbers.Length > 0
                    && locator.PageNumbers.All(page => page > 0) && locator.PageNumbers.Distinct().Count() == locator.PageNumbers.Length
                    && locator.Sections.All(section => !string.IsNullOrWhiteSpace(section)), $"Invalid locator for {entry.CaseId}.", keyPath);
        }
        foreach (var item in cases.Cases)
        {
            Require(joined.TryGetValue(item.Id, out var reference), $"Missing key entry for {item.Id}.", keyPath);
            if (!item.Enabled) continue;
            Require(reference!.GroundTruthStatus == "verified_source" && reference.ExpectedAnswerKind is not null
                && !string.IsNullOrWhiteSpace(reference.ReferenceAnswer) && reference.RequiredFacts.Length > 0,
                $"Enabled case {item.Id} needs verified ground truth, kind, answer and facts.", keyPath);
            Require(reference.ExpectedAnswerKind != "answered" || reference.SourceReferences.Length > 0,
                $"Answered case {item.Id} needs source locators.", keyPath);
        }
        try
        {
            Require(File.ReadLines(rubricPath).FirstOrDefault()?.Trim() == "# RAG evaluation rubric v2.0", "Unsupported rubric version.", rubricPath);
            return new(cases, key, casesPath, keyPath, rubricPath, EvaluationJson.Hash(casesPath), EvaluationJson.Hash(keyPath), EvaluationJson.Hash(rubricPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new EvaluationInputException($"Cannot read rubric at {rubricPath}: {error.GetType().Name}."); }
    }

    public Selection Select(string suite, string? ids)
    {
        if (!Cases.Suites.ContainsKey(suite)) throw new EvaluationInputException($"Unknown suite '{suite}'; use smoke or full. [{CasesPath}]");
        var selected = ids is null ? Cases.Suites[suite] : ids.Split(',').Select(id => id.Trim()).ToArray();
        if (selected.Length == 0 || selected.Any(string.IsNullOrWhiteSpace) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new EvaluationInputException($"Selected IDs must be nonempty and unique. [{CasesPath}]");
        var index = Cases.Cases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var id in selected)
            if (!index.ContainsKey(id)) throw new EvaluationInputException($"Unknown selected case ID '{id}'. [{CasesPath}]");
        return new(selected.Select(id => index[id]).ToArray(), ids is null ? suite : "ids");
    }

    public static bool ValidQuestion(string question) => !string.IsNullOrWhiteSpace(question) && question.Length <= 2000;
}
