using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgenticRAGProd.Evaluation;

public static class Assessment
{
    public const string Pass = "pass", Fail = "fail", NotAssessed = "not_assessed", NotApplicable = "not_applicable";
    public static readonly string[] Statuses = [Pass, Fail, NotAssessed, NotApplicable];
    public static string Aggregate(IEnumerable<CheckResult> results)
    {
        var checks = results.ToArray();
        if (checks.Any(check => check.Status == Fail)) return Fail;
        if (checks.Length == 0 || checks.All(check => check.Status == NotApplicable)
            || checks.Any(check => check.Status == NotAssessed)) return NotAssessed;
        return Pass;
    }
}

public sealed record CheckResult(string Status, string Reason)
{
    public static CheckResult Pass(string reason) => new(Assessment.Pass, reason);
    public static CheckResult Fail(string reason) => new(Assessment.Fail, reason);
    public static CheckResult Unknown(string reason) => new(Assessment.NotAssessed, reason);
    public static CheckResult NotApplicable(string reason) => new(Assessment.NotApplicable, reason);
}
public sealed record EvaluationDiagnostic(string Code, string Message, string? CaseId = null);
public sealed record CaseScore(int SchemaVersion, string RunId, string CaseId, string Question,
    string Verdict, Dictionary<string, CheckResult> Checks, string? ExpectedAnswerKind,
    string? ObservedAnswerKind, string? ObservationRunId, double? ClientElapsedMs,
    JudgeAttempt? Judge, string? DisabledReason = null);

public static partial class Scoring
{
    public static readonly string[] SemanticNames = ["correctness_completeness", "groundedness", "relevance", "citation_support"];
    public static bool SemanticApplies(string name, string expectedKind) => expectedKind == "answered"
        || name is "correctness_completeness" or "relevance"
        || name == "groundedness" && expectedKind == "insufficient_evidence";

    public static CheckResult Transport(RequestObservation request)
    {
        if (!request.Attempted) return CheckResult.Unknown("Target request was not attempted.");
        if (!string.IsNullOrWhiteSpace(request.Error)) return CheckResult.Fail($"Recorded request error: {request.Error}.");
        if (request.StatusCode is null) return CheckResult.Unknown("HTTP status is unavailable.");
        if (request.StatusCode != 200) return CheckResult.Fail($"Expected HTTP 200; observed {request.StatusCode}.");
        if (request.ValidJson == false) return CheckResult.Fail("Response body is not valid JSON.");
        if (request.ValidJson is null) return CheckResult.Unknown("Response JSON validity is unavailable.");
        return CheckResult.Pass("HTTP 200, valid JSON, no recorded transport error.");
    }

    public static CheckResult ResponseContract(RequestObservation request)
    {
        if (!request.Attempted || !request.Actual.HasValue) return CheckResult.Unknown("Response JSON is unavailable.");
        var actual = request.Actual.Value;
        if (actual.ValueKind != JsonValueKind.Object) return CheckResult.Fail("Response JSON must be an object.");
        var invalid = new List<string>(); var missing = new List<string>();
        TextField(actual, "conversationId", invalid, missing);
        TextField(actual, "messageId", invalid, missing);
        TextField(actual, "answer", invalid, missing);
        var conversation = EvaluationJson.Text(actual, "conversationId");
        if (!string.IsNullOrWhiteSpace(conversation) && (!Guid.TryParse(conversation, out var id) || id == Guid.Empty)) invalid.Add("Invalid conversationId");
        if (actual.TryGetProperty("answerKind", out var kind) && kind.ValueKind != JsonValueKind.Null
            && (kind.ValueKind != JsonValueKind.String || !EvaluationDataset.Kinds.Contains(kind.GetString()))) invalid.Add("Invalid lowercase answerKind");
        if (!actual.TryGetProperty("sources", out var sources) || sources.ValueKind == JsonValueKind.Null) missing.Add("sources");
        else if (sources.ValueKind != JsonValueKind.Array) invalid.Add("sources must be an array");
        else foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) { invalid.Add("Source must be an object"); continue; }
            foreach (var field in new[] { "chunkId", "documentName", "passage", "documentId" }) TextField(source, field, invalid, missing);
            foreach (var field in new[] { "number", "pageNumber" })
            {
                if (!source.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) missing.Add($"source.{field}");
                else if (EvaluationJson.Integer(source, field) is not > 0) invalid.Add($"source.{field} must be a positive integer");
            }
            if (!source.TryGetProperty("hasUnprocessedImages", out var image)) missing.Add("source.hasUnprocessedImages");
            else if (image.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) invalid.Add("source.hasUnprocessedImages must be boolean");
            if (source.TryGetProperty("context", out var context) && context.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) invalid.Add("source.context must be an object or null");
        }
        return Issues(invalid, missing, "Required identifiers, answer and sources shape are valid. Missing legacy answerKind is assessed separately.");
    }

    public static string? ObservedKind(RequestObservation request) => request.Actual is { } actual ? EvaluationJson.Text(actual, "answerKind") : null;

    public static CheckResult AnswerKind(RequestObservation request, string expected)
    {
        if (request.Actual is not { ValueKind: JsonValueKind.Object } actual
            || !actual.TryGetProperty("answerKind", out var value) || value.ValueKind == JsonValueKind.Null)
            return CheckResult.Unknown("Observed answerKind is missing; expected labels and text heuristics are not observations.");
        var kind = ObservedKind(request);
        if (kind is null || !EvaluationDataset.Kinds.Contains(kind)) return CheckResult.Fail("Observed answerKind is not an accepted lowercase label.");
        return kind == expected ? CheckResult.Pass($"Observed {kind} matches the reference kind.")
            : CheckResult.Fail($"Observed {kind}; expected {expected}.");
    }

    public static CheckResult CitationReferences(RequestObservation request)
    {
        if (request.Actual is not { ValueKind: JsonValueKind.Object } actual) return CheckResult.Unknown("Response JSON is unavailable.");
        var answer = EvaluationJson.Text(actual, "answer");
        if (answer is null || !actual.TryGetProperty("sources", out var sources)) return CheckResult.Unknown("Answer or sources are missing.");
        if (sources.ValueKind != JsonValueKind.Array) return CheckResult.Fail("Sources are not an array.");
        var numbers = new HashSet<int>(); var invalid = new List<string>(); var missing = new List<string>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) { invalid.Add("Invalid source object"); continue; }
            if (!source.TryGetProperty("number", out var number) || number.ValueKind == JsonValueKind.Null) { missing.Add("Source number"); continue; }
            var value = EvaluationJson.Integer(source, "number");
            if (value is not > 0) invalid.Add("Source numbers must be positive integers");
            else if (!numbers.Add(value.Value)) invalid.Add("Duplicate source number");
        }
        var citations = NumericCitations().Matches(answer);
        foreach (Match citation in citations)
        {
            var body = citation.Groups[1].Value;
            if (!body.All(char.IsAsciiDigit) || !int.TryParse(body, out var number) || number <= 0)
                invalid.Add($"Invalid numeric citation {citation.Value}");
            else if (!numbers.Contains(number))
            {
                if (missing.Count > 0) missing.Add("A citation cannot be resolved because source numbering is missing");
                else invalid.Add($"Citation {citation.Value} has no available source");
            }
        }
        var kind = ObservedKind(request);
        if (kind == "answered" && citations.Count == 0) invalid.Add("Answered response has no numbered citation");
        if (kind == "clarification" && (citations.Count > 0 || sources.GetArrayLength() > 0)) invalid.Add("Clarification must have no citations and empty sources");
        if (kind is null || !EvaluationDataset.Kinds.Contains(kind)) missing.Add("Observed kind is unavailable for response-specific citation rules; existing references were still inspected");
        return Issues(invalid, missing, "Numbered references resolve to unique positive source numbers; applicable presence/clarification rules hold. This does not establish citation support.");
    }

    public static CheckResult SourceLocator(RequestObservation request, AnswerReference reference)
    {
        if (reference.ExpectedAnswerKind != "answered") return CheckResult.NotApplicable("Source-locator checks apply only to answered reference cases.");
        if (reference.SourceReferences.Length == 0) return CheckResult.Unknown("No reference source locator is available.");
        if (request.Actual is not { ValueKind: JsonValueKind.Object } actual || !actual.TryGetProperty("sources", out var sources))
            return CheckResult.Unknown("Returned evidence sources are unavailable.");
        if (sources.ValueKind != JsonValueKind.Array) return CheckResult.Fail("Returned sources are not an array.");
        var incomplete = false;
        foreach (var source in sources.EnumerateArray())
        {
            var name = EvaluationJson.Text(source, "documentName");
            int? first = null, last = null;
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                first = EvaluationJson.Integer(context, "pageStart"); last = EvaluationJson.Integer(context, "pageEnd");
                if (first is not > 0 || last < first || last is null) { first = null; last = null; }
            }
            first ??= EvaluationJson.Integer(source, "pageNumber"); last ??= first;
            if (string.IsNullOrWhiteSpace(name) || first is not > 0 || last is null) { incomplete = true; continue; }
            foreach (var expected in reference.SourceReferences)
                if (string.Equals(Basename(name), Basename(expected.DocumentName), StringComparison.OrdinalIgnoreCase)
                    && expected.PageNumbers.Any(page => page >= first && page <= last))
                    return CheckResult.Pass($"Returned evidence matches {Basename(expected.DocumentName)} on an allowed page. Sections are informational; this is not relevance, factual support or raw-retrieval recall@k.");
        }
        return incomplete ? CheckResult.Unknown("Some returned sources lack usable document/page metadata; no confirmed locator hit.")
            : CheckResult.Fail("No returned evidence source matches an expected document and alternative allowed page.");
    }

    public static CheckResult ScenarioSetup(EvaluationCase item, Observation? observation)
    {
        if (item.SetupTurns.Length == 0)
        {
            if (observation?.SetupTurns.Length > 0 || observation is { SetupCaptured: true, Target.ConversationId: not null })
                return CheckResult.Fail("A fresh-conversation case unexpectedly used setup turns or an existing conversation.");
            return CheckResult.NotApplicable("This case has no setup turns.");
        }
        if (observation is null || !observation.SetupCaptured) return CheckResult.Unknown("Actual setup conversation was not captured.");
        if (observation.BlockedReason is not null) return CheckResult.Fail(observation.BlockedReason);
        if (observation.SetupTurns.Length != item.SetupTurns.Length) return CheckResult.Unknown("Setup observations are incomplete.");
        string? previous = null;
        for (var index = 0; index < item.SetupTurns.Length; index++)
        {
            var turn = observation.SetupTurns[index];
            if (turn.Question.Trim() != item.SetupTurns[index].Trim()) return CheckResult.Fail("Captured setup question does not match the scenario.");
            if (!CaptureRunner.SuccessfulSetup(turn)) return CheckResult.Fail($"Setup turn {index + 1} did not succeed.");
            if (!SameConversation(turn.ConversationId, previous)) return CheckResult.Fail("Setup did not continue the actual returned conversationId.");
            previous = EvaluationJson.Text(turn.Actual!.Value, "conversationId");
        }
        return SameConversation(observation.Target.ConversationId, previous)
            ? CheckResult.Pass("Setup succeeded and target reused the actual returned conversationId.")
            : CheckResult.Fail("Target did not reuse the actual setup conversationId.");
    }

    public static async Task<CaseScore> ScoreAsync(string runId, EvaluationCase item, AnswerReference reference,
        Observation? observation, IAnswerJudge? judge, List<EvaluationDiagnostic> diagnostics)
    {
        if (!item.Enabled) return new(1, runId, item.Id, item.Question, "skipped", [], reference.ExpectedAnswerKind, null, null, null, null, item.DisabledReason);
        var mismatch = observation is not null && (observation.Question.Trim() != item.Question.Trim()
            || observation.Target.Question.Trim() != item.Question.Trim());
        if (mismatch)
        {
            diagnostics.Add(new("question_mismatch", "Captured question differs under the same case ID; refused comparison after trimming outer whitespace only.", item.Id));
            observation = null;
        }
        var target = observation?.Target ?? new RequestObservation(item.Question, null, false, null, null, null, null, null, null);
        var checks = new Dictionary<string, CheckResult>(StringComparer.Ordinal)
        {
            ["scenario_setup"] = ScenarioSetup(item, observation),
            ["transport"] = Transport(target),
            ["response_contract"] = ResponseContract(target),
            ["answer_kind"] = AnswerKind(target, reference.ExpectedAnswerKind!),
            ["citation_reference_integrity"] = CitationReferences(target),
            ["source_locator_hit"] = SourceLocator(target, reference)
        };
        JudgeAttempt? attempt = null;
        var eligible = checks["transport"].Status == Assessment.Pass && checks["response_contract"].Status == Assessment.Pass
            && checks["scenario_setup"].Status is Assessment.Pass or Assessment.NotApplicable;
        if (judge is not null && eligible)
        {
            var actual = target.Actual!.Value;
            var input = new JudgeInput(item.Question,
                observation!.SetupTurns.Select(turn => new SetupConversation(turn.Question, EvaluationJson.Text(turn.Actual!.Value, "answer")!)).ToArray(),
                reference.ExpectedAnswerKind!, ObservedKind(target), reference.ReferenceAnswer!, reference.RequiredFacts,
                reference.ForbiddenClaims, reference.GradingNotes, EvaluationJson.Text(actual, "answer")!,
                actual.GetProperty("sources").EnumerateArray().Select(source => source.Clone()).ToArray(), EvaluationDataset.RubricVersion);
            try { attempt = await judge.JudgeAsync(input); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var message = $"Judge failed: {error.GetType().Name}.";
                attempt = new(SemanticNames.ToDictionary(name => name, _ => CheckResult.Unknown(message)),
                    judge.Identity, StructuredJudge.PromptHash, null, null, Assessment.NotAssessed, message);
            }
            if (attempt is { Error: null } && SemanticNames.Any(name => !attempt.Checks.TryGetValue(name, out var check)
                || !Assessment.Statuses.Contains(check.Status) || string.IsNullOrWhiteSpace(check.Reason)
                || check.Status == Assessment.NotApplicable && SemanticApplies(name, reference.ExpectedAnswerKind!)))
                attempt = attempt with { Error = "judge_error: invalid or missing per-check judgment." };
            if (attempt?.Error is not null) diagnostics.Add(new("judge_error", attempt.Error, item.Id));
        }
        foreach (var name in SemanticNames)
        {
            if (!SemanticApplies(name, reference.ExpectedAnswerKind!))
                checks[name] = CheckResult.NotApplicable($"Not required for expected {reference.ExpectedAnswerKind} behavior.");
            else if (attempt is { Error: null } && attempt.Checks.TryGetValue(name, out var result)
                && result.Status is Assessment.Pass or Assessment.Fail or Assessment.NotAssessed && !string.IsNullOrWhiteSpace(result.Reason))
                checks[name] = result;
            else checks[name] = CheckResult.Unknown(judge is null ? "Semantic judging is disabled; keyword presence is not factual correctness."
                : eligible ? "Judge unavailable, failed or returned an invalid judgment." : "Required actual answer/evidence/setup inputs are unavailable.");
        }
        return new(1, runId, item.Id, item.Question, Assessment.Aggregate(checks.Values), checks,
            reference.ExpectedAnswerKind, ObservedKind(target), observation?.RunId, target.ClientElapsedMs, attempt);
    }

    private static void TextField(JsonElement value, string name, List<string> invalid, List<string> missing)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) missing.Add(name);
        else if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) invalid.Add($"Invalid nonempty {name}");
    }
    private static CheckResult Issues(List<string> invalid, List<string> missing, string success) => invalid.Count > 0
        ? CheckResult.Fail(string.Join("; ", invalid.Distinct())) : missing.Count > 0
            ? CheckResult.Unknown("Missing assessment inputs: " + string.Join("; ", missing.Distinct())) : CheckResult.Pass(success);
    private static string Basename(string name) => name.Trim().Replace('\\', '/').Split('/')[^1];
    private static bool SameConversation(string? actual, string? expected) => actual is null && expected is null
        || Guid.TryParse(actual, out var left) && Guid.TryParse(expected, out var right) && left == right;
    [GeneratedRegex(@"\[([+-]?\d[^\]\r\n]*)\]")] private static partial Regex NumericCitations();
}
