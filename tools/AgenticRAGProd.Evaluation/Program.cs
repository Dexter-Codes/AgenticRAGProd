using System.Text.Json;

namespace AgenticRAGProd.Evaluation;

public static class Program
{
    public static Task<int> Main(string[] args) => EvaluationCli.ExecuteAsync(args, Console.Out);
}

public sealed class EvaluationOptions
{
    public string Command { get; private set; } = "validate";
    public string? LegacyPath { get; private set; }
    public string Suite { get; private set; } = "full";
    public string? Ids { get; private set; }
    public string CasesPath { get; private set; } = "";
    public string KeyPath { get; private set; } = "";
    public string RubricPath { get; private set; } = "";
    public string? Input { get; private set; }
    public string Output { get; private set; } = "";
    public string? Settings { get; private set; }
    public string BaseUrl { get; private set; } = "http://localhost:5080/";
    public string Judge { get; private set; } = "none";
    public bool Live { get; private set; }
    public bool AllowModelCalls { get; private set; }
    public bool Help { get; private set; }

    public static EvaluationOptions Parse(string[] args)
    {
        var result = new EvaluationOptions();
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"]) { result.Help = true; return result; }
        if (args[0] is "validate" or "score" or "run") result.Command = args[0];
        else if (!args[0].StartsWith('-')) { result.Command = "capture"; result.LegacyPath = Path.GetFullPath(args[0]); }
        else throw new EvaluationInputException("Use validate, score, run --live, or the legacy positional cases path for live capture only.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        string[] options = ["--suite", "--ids", "--cases", "--key", "--rubric", "--input", "--output", "--base-url", "--judge", "--settings"];
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--live" or "--allow-model-calls")
            {
                if (!flags.Add(name)) throw new EvaluationInputException($"Duplicate option {name}.");
            }
            else if (options.Contains(name) && index + 1 < args.Length && !args[index + 1].StartsWith("--"))
            {
                if (!values.TryAdd(name, args[++index])) throw new EvaluationInputException($"Duplicate option {name}.");
            }
            else throw new EvaluationInputException($"Unknown option or missing value: {name}.");
        }
        var assets = AssetDirectory();
        result.CasesPath = Path.GetFullPath(values.GetValueOrDefault("--cases", Path.Combine(assets, "cases.v2.json")));
        var besideCases = Path.GetDirectoryName(result.CasesPath)!;
        result.KeyPath = Path.GetFullPath(values.GetValueOrDefault("--key", Path.Combine(besideCases, "answer-key.v2.json")));
        result.RubricPath = Path.GetFullPath(values.GetValueOrDefault("--rubric", Path.Combine(besideCases, "RUBRIC.md")));
        result.Output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(assets, "runs")));
        result.Input = values.TryGetValue("--input", out var input) ? Path.GetFullPath(input) : null;
        result.Settings = values.TryGetValue("--settings", out var settings) ? Path.GetFullPath(settings) : null;
        result.Suite = values.GetValueOrDefault("--suite", "full"); result.Ids = values.GetValueOrDefault("--ids");
        result.Judge = values.GetValueOrDefault("--judge", "none");
        result.Live = flags.Contains("--live"); result.AllowModelCalls = flags.Contains("--allow-model-calls");
        if (result.Judge is not ("none" or "llm")) throw new EvaluationInputException("--judge must be none or llm.");
        if (result.Judge == "llm" && !result.AllowModelCalls) throw new EvaluationInputException("--judge llm requires explicit --allow-model-calls; no model calls were made.");
        if (result.Command == "run" && !result.Live) throw new EvaluationInputException("Live capture requires explicit --live; no answer API calls were made.");
        if (result.Command is "validate" or "score" && result.Live) throw new EvaluationInputException("--live is only valid for run or legacy capture.");
        if (result.Command == "score" && result.Input is null) throw new EvaluationInputException("score requires --input <observations.jsonl>.");
        if (result.Command != "score" && result.Input is not null) throw new EvaluationInputException("--input is only valid for score.");
        if (result.Command is "validate" or "capture" && result.Judge != "none") throw new EvaluationInputException("Judging applies only to score or run; legacy mode is capture-only.");
        if (result.Command == "capture" && values.ContainsKey("--suite")) throw new EvaluationInputException("Legacy capture uses its positional file and optional --ids, not --suite.");
        var url = values.GetValueOrDefault("--base-url", result.BaseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")
            || baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
            throw new EvaluationInputException("--base-url must be an absolute HTTP(S) service URL without credentials, query or fragment.");
        result.BaseUrl = baseUri.AbsoluteUri.TrimEnd('/') + "/";
        return result;
    }

    public static string AssetDirectory()
    {
        // Prefer checkout assets over copies beside a test/executable output directory.
        foreach (var relative in new[] { Path.Combine("tools", "AgenticRAGProd.Evaluation", "Eval"), "Eval" })
            foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    var candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(Path.Combine(candidate, "cases.v2.json"))) return candidate;
                }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Eval"));
    }
}

public static class EvaluationCli
{
    public const string Help = """
        RAG evaluation (offline by default)
          validate [--suite smoke|full] [--ids case-001,case-002]
          score --input <JSONL> [--output <directory>] [--suite smoke|full] [--ids ...]
          run --live [--base-url http://localhost:5080/] [--suite smoke|full] [--ids ...]
          <legacy-cases.json> [--live] [--base-url ...] [--output <directory>]   (legacy live capture only)
        Shared: --cases <path> --key <path> --rubric <path> --settings <non-secret-provenance.json>
        Optional score/run judge: --judge llm --allow-model-calls (separate evaluator environment settings required).
        Paths are relative to the current working directory; --output is a root for new unique run directories.
        Exit: 0 complete pass/valid dataset, 1 assessed failure, 2 input/tool error, 3 incomplete assessment.
        """;

    public static async Task<int> ExecuteAsync(string[] args, TextWriter output,
        HttpClient? answerClient = null, IAnswerJudge? judgeDouble = null)
    {
        try
        {
            var options = EvaluationOptions.Parse(args);
            if (options.Help) { await output.WriteLineAsync(Help); return 0; }
            var dataset = EvaluationDataset.Load(options.CasesPath, options.KeyPath, options.RubricPath);
            var selection = options.Command == "capture" ? LegacySelection(options, dataset) : dataset.Select(options.Suite, options.Ids);
            await output.WriteLineAsync($"Cases: {dataset.CasesPath}\nKey: {dataset.KeyPath}\nRubric: {dataset.RubricPath}");
            await output.WriteLineAsync($"Dataset {dataset.Cases.DatasetVersion}: {dataset.Cases.Cases.Length} cases, {dataset.Cases.Cases.Count(item => !item.Enabled)} disabled. Selected {selection.Cases.Length}; enabled {selection.Enabled.Length}; planned answer requests {selection.PlannedRequests}.");
            if (options.Command == "validate")
            {
                await output.WriteLineAsync($"VALID. Dataset SHA256 {dataset.CasesHash}; key SHA256 {dataset.KeyHash}. No API/model calls.");
                return 0;
            }
            var provenance = Provenance.Read(options.Settings);
            var observations = options.Command == "score" ? ObservationReader.Read(options.Input!) : [];
            // Factory creation validates configuration only. Actual SDK construction is lazy inside the judge call.
            var judge = options.Judge == "llm" ? judgeDouble ?? AzureJudge.Create() : null;
            var independence = judge is null ? "not_assessed" : SameKnown(judge.Identity.Deployment, provenance["answerDeployment"])
                || SameKnown(judge.Identity.Model, provenance["answerModel"]) ? "same_model_or_deployment: not independent validation" : "unknown: different deployment names do not establish independence";
            var run = new RunMetadata(1, "1.0.0", Guid.NewGuid().ToString("N"), options.Command, DateTimeOffset.UtcNow,
                options.Command == "capture" ? "legacy-unversioned" : dataset.Cases.DatasetVersion,
                options.Command == "capture" ? EvaluationJson.Hash(options.LegacyPath!) : dataset.CasesHash,
                options.Command == "capture" ? "unknown" : dataset.KeyHash, EvaluationDataset.RubricVersion, dataset.RubricHash,
                options.LegacyPath ?? dataset.CasesPath, dataset.KeyPath, dataset.RubricPath, selection.Label,
                selection.Cases.Select(item => item.Id).ToArray(), Provenance.ClientCommit(Environment.CurrentDirectory), provenance,
                options.Command == "score" ? "unknown" : options.BaseUrl, options.Input ?? "unknown",
                options.Input is null ? "unknown" : EvaluationJson.Hash(options.Input), options.Judge, judge?.Identity,
                judge is null ? "unknown" : StructuredJudge.PromptHash, independence);
            using var artifacts = new ArtifactWriter(options.Output, run);
            await output.WriteLineAsync($"Output: {artifacts.DirectoryPath}");
            if (options.Command is "run" or "capture")
            {
                using var ownedClient = answerClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromSeconds(250) } : null;
                var client = answerClient ?? ownedClient!;
                if (client.BaseAddress is null) client.BaseAddress = new Uri(options.BaseUrl);
                var captured = new List<Observation>();
                var runner = new CaptureRunner(client);
                foreach (var item in selection.Enabled)
                {
                    var observation = await runner.CaptureAsync(item, run);
                    captured.Add(observation); artifacts.Observation(observation);
                    await output.WriteLineAsync($"{item.Id}: target HTTP {observation.Target.StatusCode?.ToString() ?? "unknown"}; setup turns {observation.SetupTurns.Length}; blocked={observation.BlockedReason is not null}");
                }
                observations = captured.ToArray();
            }
            else foreach (var observation in observations) artifacts.Observation(observation);
            if (options.Command == "capture")
            {
                var exit = observations.Any(item => Scoring.Transport(item.Target).Status == Assessment.Fail || item.BlockedReason is not null) ? 1 : 3;
                artifacts.CompleteCapture(run, selection, observations, exit);
                await output.WriteLineAsync($"Capture only; no quality scores. Observations: {Path.Combine(artifacts.DirectoryPath, "observations.jsonl")}. Exit {exit}.");
                return exit;
            }
            var diagnostics = new List<EvaluationDiagnostic>();
            foreach (var observation in observations)
                if (!dataset.Cases.Cases.Any(item => item.Id == observation.CaseId)) diagnostics.Add(new("unknown_case", "Observation ID is not in this dataset.", observation.CaseId));
            var scores = new List<CaseScore>();
            foreach (var item in selection.Cases)
            {
                var reference = dataset.Key.Entries.Single(entry => entry.CaseId == item.Id);
                var score = await Scoring.ScoreAsync(run.RunId, item, reference, observations.SingleOrDefault(value => value.CaseId == item.Id), judge, diagnostics);
                scores.Add(score); artifacts.Score(score);
            }
            var summary = Reports.Summarize(run, dataset, selection, observations, scores, diagnostics);
            artifacts.Complete(summary);
            await output.WriteLineAsync($"{summary.Pass} pass; {summary.Fail} fail; {summary.NotAssessed} not_assessed; {summary.Missing} missing; {summary.Skipped} dataset-disabled. Exit {summary.ExitCode}.");
            await output.WriteLineAsync($"Report: {Path.Combine(artifacts.DirectoryPath, "report.md")}");
            return summary.ExitCode;
        }
        catch (Exception error) when (error is EvaluationInputException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await output.WriteLineAsync($"ERROR: {error.Message}");
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await output.WriteLineAsync($"ERROR: Evaluation tool execution failed: {error.GetType().Name}. No successful verdict was assigned.");
            return 2;
        }
    }

    private static bool SameKnown(string left, string right) => left != "unknown" && right != "unknown" && string.Equals(left, right, StringComparison.Ordinal);
    private sealed record LegacyCase(string Id, string Question, bool Enabled = true, string? DisabledReason = null, string[]? SetupTurns = null);
    private static Selection LegacySelection(EvaluationOptions options, EvaluationDataset dataset)
    {
        var old = EvaluationJson.Read<LegacyCase[]>(options.LegacyPath!);
        if (old.Length == 0 || old.Any(item => string.IsNullOrWhiteSpace(item.Id) || !EvaluationDataset.ValidQuestion(item.Question))
            || old.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != old.Length)
            throw new EvaluationInputException($"Invalid or duplicate legacy cases. [{options.LegacyPath}]");
        var selectedIds = options.Ids?.Split(',').Select(id => id.Trim()).ToArray() ?? old.Select(item => item.Id).ToArray();
        if (selectedIds.Any(id => !old.Any(item => item.Id == id)) || selectedIds.Distinct().Count() != selectedIds.Length)
            throw new EvaluationInputException($"Unknown or duplicate selected legacy IDs. [{options.LegacyPath}]");
        var selected = selectedIds.Select(id => old.Single(item => item.Id == id)).Select(item =>
        {
            var disabled = dataset.Cases.Cases.FirstOrDefault(value => value.Id == item.Id && !value.Enabled);
            if (item.SetupTurns?.Any(question => !EvaluationDataset.ValidQuestion(question)) == true)
                throw new EvaluationInputException($"Invalid legacy setup turn for {item.Id}. [{options.LegacyPath}]");
            return new EvaluationCase(item.Id, item.Question, item.Enabled && disabled is null,
                disabled?.DisabledReason ?? (item.Enabled ? null : item.DisabledReason ?? "Disabled in legacy input."), [], item.SetupTurns ?? []);
        }).ToArray();
        return new(selected, "legacy");
    }
}
