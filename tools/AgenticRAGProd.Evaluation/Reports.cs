using System.Text;
using System.Text.Json;

namespace AgenticRAGProd.Evaluation;

public sealed record RunMetadata(int SchemaVersion, string EvaluationVersion, string RunId, string Mode,
    DateTimeOffset StartedAtUtc, string DatasetVersion, string DatasetHash, string AnswerKeyHash,
    string RubricVersion, string RubricHash, string CasesPath, string KeyPath, string RubricPath,
    string Suite, string[] SelectedIds, string ClientCommit, Dictionary<string, string> AnswerServiceProvenance,
    string BaseUrl, string InputPath, string InputHash, string JudgeMode, JudgeIdentity? Judge,
    string JudgePromptHash, string JudgeIndependence);
public sealed record CheckSummary(int Pass, int Fail, int NotAssessed, int NotApplicable,
    int Assessed, int Eligible, double? PassRate, double? AssessmentCoverage);
public sealed record DisabledCase(string CaseId, string Reason, bool Selected);
public sealed record EvaluationSummary(int SchemaVersion, RunMetadata Run, string Verdict, int ExitCode,
    int Selected, int Enabled, int Observed, int Missing, int Skipped, int SelectedSkipped, int Errors,
    int Pass, int Fail, int NotAssessed, int SetupRequests, int TargetRequests, int JudgeCalls,
    int ClientLatencySamples, double? ClientLatencyMedianMs, double? ClientLatencyMaxMs,
    Dictionary<string, CheckSummary> Checks, DisabledCase[] DisabledCases, EvaluationDiagnostic[] Diagnostics);

public sealed class ArtifactWriter : IDisposable
{
    private readonly StreamWriter _observations;
    private readonly StreamWriter _scores;
    public string DirectoryPath { get; }

    public ArtifactWriter(string outputRoot, RunMetadata run)
    {
        var root = Path.GetFullPath(outputRoot);
        if (File.Exists(root)) throw new EvaluationInputException($"Output must be a directory: {root}");
        DirectoryPath = Path.Combine(root, run.RunId);
        if (Directory.Exists(DirectoryPath)) throw new EvaluationInputException($"Refusing to overwrite an existing run: {DirectoryPath}");
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, ".gitignore"), "*\n");
        _observations = NewLines("observations.jsonl");
        _scores = NewLines("scores.jsonl");
        WriteNew("run.json", run);
    }

    public void Observation(Observation observation) => _observations.WriteLine(JsonSerializer.Serialize(observation, EvaluationJson.Options));
    public void Score(CaseScore score) => _scores.WriteLine(JsonSerializer.Serialize(score, EvaluationJson.Options));
    public void Complete(EvaluationSummary summary)
    {
        WriteNew("summary.json", summary);
        using var report = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "report.md"), FileMode.CreateNew));
        report.Write(Reports.Markdown(summary));
    }
    public void CompleteCapture(RunMetadata run, Selection selection, Observation[] observations, int exitCode)
    {
        WriteNew("summary.json", new { schemaVersion = 1, run, captureOnly = true, assessment = Assessment.NotAssessed,
            exitCode, selected = selection.Cases.Length, enabled = selection.Enabled.Length,
            skipped = selection.Cases.Where(item => !item.Enabled).Select(item => new { item.Id, item.DisabledReason }),
            observed = observations.Length, targetRequests = observations.Count(item => item.Target.Attempted),
            setupRequests = observations.Sum(item => item.SetupTurns.Length), judgeCalls = 0 });
        using var report = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "report.md"), FileMode.CreateNew));
        report.WriteLine($"# Legacy capture\n\nRun `{run.RunId}`. Captured {observations.Length} cases. No reference-based scores or semantic judgments were performed. Assessment: not_assessed. Exit: {exitCode}.");
    }
    private StreamWriter NewLines(string name) => new(new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    private void WriteNew<T>(string name, T value)
    {
        using var stream = new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions(EvaluationJson.Options) { WriteIndented = true });
    }
    public void Dispose() { _observations.Dispose(); _scores.Dispose(); }
}

public static class Reports
{
    // Infrastructure/input errors take precedence; per-case failures remain visible in every report.
    public static int ExitCode(bool executionError, int failures, int incomplete, int enabled) => executionError ? 2
        : failures > 0 ? 1 : incomplete > 0 || enabled == 0 ? 3 : 0;

    public static EvaluationSummary Summarize(RunMetadata run, EvaluationDataset dataset, Selection selection,
        IReadOnlyList<Observation> observations, IReadOnlyList<CaseScore> scores, IReadOnlyList<EvaluationDiagnostic> diagnostics)
    {
        var enabled = scores.Where(score => score.Verdict != "skipped").ToArray();
        var matched = observations.Where(observation => selection.Enabled.Any(item => item.Id == observation.CaseId
            && item.Question.Trim() == observation.Question.Trim() && item.Question.Trim() == observation.Target.Question.Trim())).ToArray();
        var latency = matched.Where(item => item.Target.ClientElapsedMs is >= 0)
            .Select(item => item.Target.ClientElapsedMs!.Value).Where(double.IsFinite).Order().ToArray();
        var checks = enabled.SelectMany(score => score.Checks).Select(pair => pair.Key).Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => Counts(enabled.Where(score => score.Checks.ContainsKey(name)).Select(score => score.Checks[name])), StringComparer.Ordinal);
        var pass = enabled.Count(score => score.Verdict == Assessment.Pass);
        var fail = enabled.Count(score => score.Verdict == Assessment.Fail);
        var unknown = enabled.Count(score => score.Verdict == Assessment.NotAssessed);
        var disabled = dataset.Cases.Cases.Where(item => !item.Enabled)
            .Select(item => new DisabledCase(item.Id, item.DisabledReason!, selection.Cases.Any(selected => selected.Id == item.Id))).ToArray();
        var errors = enabled.Count(score => score.Checks["transport"].Status == Assessment.Fail || score.Checks["scenario_setup"].Status == Assessment.Fail
            || diagnostics.Any(error => error.CaseId == score.CaseId));
        var exit = ExitCode(diagnostics.Count > 0, fail, unknown, enabled.Length);
        return new(1, run, fail > 0 ? Assessment.Fail : exit != 0 ? Assessment.NotAssessed : Assessment.Pass, exit,
            selection.Cases.Length, enabled.Length, matched.Length, enabled.Length - matched.Length,
            disabled.Length, selection.Cases.Count(item => !item.Enabled), errors,
            pass, fail, unknown, matched.Sum(item => item.SetupTurns.Count(turn => turn.Attempted)),
            matched.Count(item => item.Target.Attempted), scores.Count(score => score.Judge is not null),
            latency.Length, latency.Length == 0 ? null : (latency[(latency.Length - 1) / 2] + latency[latency.Length / 2]) / 2,
            latency.Length == 0 ? null : latency[^1], checks, disabled, diagnostics.ToArray());
    }

    private static CheckSummary Counts(IEnumerable<CheckResult> checks)
    {
        var values = checks.ToArray();
        var pass = values.Count(value => value.Status == Assessment.Pass);
        var fail = values.Count(value => value.Status == Assessment.Fail);
        var missing = values.Count(value => value.Status == Assessment.NotAssessed);
        var na = values.Count(value => value.Status == Assessment.NotApplicable);
        return new(pass, fail, missing, na, pass + fail, pass + fail + missing,
            pass + fail == 0 ? null : (double)pass / (pass + fail),
            pass + fail + missing == 0 ? null : (double)(pass + fail) / (pass + fail + missing));
    }

    public static string Markdown(EvaluationSummary summary)
    {
        static string Clean(string text) => text.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        var result = new StringBuilder($"# RAG evaluation report\n\nRun `{summary.Run.RunId}` · dataset `{summary.Run.DatasetVersion}` · rubric `{summary.Run.RubricVersion}`\n\n");
        result.AppendLine($"Verdict: **{summary.Verdict}**. Exit code: **{summary.ExitCode}**.");
        result.AppendLine($"\nSelected: {summary.Selected}; enabled: {summary.Enabled}; matched observations: {summary.Observed}; missing: {summary.Missing}; dataset-disabled: {summary.Skipped} ({summary.SelectedSkipped} selected); error cases: {summary.Errors}.");
        result.AppendLine($"\nCase verdicts: {summary.Pass} pass, {summary.Fail} fail, {summary.NotAssessed} not_assessed. Setup requests: {summary.SetupRequests}; target requests: {summary.TargetRequests}; judge calls: {summary.JudgeCalls}.");
        result.AppendLine("\n| Check | Pass / assessed | Fail | Not assessed | Not applicable | Assessment coverage |\n| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in summary.Checks)
            result.AppendLine($"| {item.Key} | {item.Value.Pass}/{item.Value.Assessed} | {item.Value.Fail} | {item.Value.NotAssessed} | {item.Value.NotApplicable} | {item.Value.Assessed}/{item.Value.Eligible} |");
        result.AppendLine("\nRates use pass+fail only; coverage uses assessed/eligible. Not-assessed checks and disabled cases are not successes. Keyword presence is not factual correctness. Source locator hits and citation-reference validity are mechanical checks, not groundedness or retrieval recall@k.");
        result.AppendLine($"\nClient target latency: {summary.ClientLatencySamples} samples; median {(summary.ClientLatencyMedianMs?.ToString("F0") ?? "unknown")} ms; maximum {(summary.ClientLatencyMaxMs?.ToString("F0") ?? "unknown")} ms. No internal stage timing, missing token usage, latency SLO or production p95 is inferred.");
        result.AppendLine($"\nJudge: {summary.Run.JudgeMode}; independence: {summary.Run.JudgeIndependence}. LLM judgments are fallible and require human audit; same-model grading is not independent validation.");
        if (summary.DisabledCases.Length > 0)
        {
            result.AppendLine("\n## Disabled cases\n");
            foreach (var item in summary.DisabledCases) result.AppendLine($"- {Clean(item.CaseId)}: {Clean(item.Reason)}");
        }
        if (summary.Diagnostics.Length > 0)
        {
            result.AppendLine("\n## Input/tool diagnostics\n");
            foreach (var item in summary.Diagnostics) result.AppendLine($"- {Clean(item.Code)} ({Clean(item.CaseId ?? "run")}): {Clean(item.Message)}");
        }
        return result.ToString();
    }
}
