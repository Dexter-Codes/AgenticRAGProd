# Automated RAG evaluation

This console tool validates the supplied v2 dataset, records explicitly enabled live answers, and scores captured observations. Default scoring is offline: semantic quality stays `not_assessed` without an explicitly enabled evaluator. `CHAT.md` describes the application behavior this tool observes. The immutable specification pack remains under `Eval/`; its README describes the original milestone rather than the completed implementation.

Run these commands from the repository root with .NET 10. `--output` names a directory containing a new unique run subdirectory on every execution. Paths are resolved relative to the current working directory; input errors include the resolved path.

```sh
dotnet build AgenticRAGProd.slnx
dotnet test tests/AgenticRAGProd.Tests --no-build --no-restore

# Validate all data and the selected suite, with no service calls.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- validate --suite full

# Replay the supplied snapshot offline. Expected exit: 3 (incomplete assessment).
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- score \
  --input tools/AgenticRAGProd.Evaluation/Eval/baseline/results.original.jsonl \
  --suite full --output tools/AgenticRAGProd.Evaluation/Eval/runs
```

For live capture, first run the existing API in its Development profile with `Chat:LocalSingleUserMode=true`, using an already indexed fixed demo/staging corpus. Its configured Azure model/search services must be available; chat messages use the local SQLite database. The evaluation tool does not upload files, reindex, provision resources or change configuration. These commands incur the ordinary answer, embedding, search and SQLite operations:

```sh
DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AgenticRAGProd.Api --no-launch-profile --urls http://localhost:5080

# In a second terminal: 5 target requests, no setup requests, no judge.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- run --live \
  --base-url http://localhost:5080/ --suite smoke

# 16 enabled targets plus the one actual setup turn for case-017: 17 requests.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- run --live \
  --base-url http://localhost:5080/ --suite full

# Explicit IDs replace suite membership; disabled cases remain disabled.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- run --live \
  --base-url http://localhost:5080/ --ids case-001,case-017
```

Check the API's printed listener URL and adjust `--base-url` if necessary. Runs are sequential, with no client retries or redirects. Every enabled case starts a fresh conversation. A scenario sends its real setup questions first and continues using each actual returned `conversationId`. Failed setup blocks its target. Only `query` and `conversationId` enter `/api/chat`; keys, references and grading instructions stay in the evaluator. The `scenario_setup` check verifies this continuation independently of target answer quality.

The previous positional invocation remains compatible as live capture-only mode. Passing a legacy cases file explicitly selects this historical network operation; `--live` is optional for this compatibility form. Use `score --input` for offline replay:

```sh
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- \
  tools/AgenticRAGProd.Evaluation/Eval/baseline/cases.original.json \
  --base-url http://localhost:5080/
```

Historical expected fields in that file are ignored, and IDs disabled by the current dataset remain disabled. Capture-only mode writes observations and an incomplete capture summary, with no reference-based scores or judge calls. Its normal exit is 3 because it supplies no quality verdict; request failures return 1.

## Optional semantic judge

The evaluator uses its own explicit environment settings; it never loads API appsettings or User Secrets. Configure an existing compatible Azure OpenAI chat deployment using your shell or secret manager:

```sh
export RAG_EVAL_JUDGE_ENDPOINT="https://YOUR-RESOURCE.openai.azure.com/"
export RAG_EVAL_JUDGE_DEPLOYMENT="YOUR-EVALUATOR-DEPLOYMENT"
export RAG_EVAL_JUDGE_MODEL="YOUR-MODEL-ID"  # optional; otherwise unknown
# Set RAG_EVAL_JUDGE_API_KEY privately through your secret manager/environment.

# Grade captured results without calling the answer API; this DOES call the judge.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- score \
  --input tools/AgenticRAGProd.Evaluation/Eval/runs/CAPTURE_RUN_ID/observations.jsonl \
  --suite full --judge llm --allow-model-calls \
  --settings tools/AgenticRAGProd.Evaluation/provenance.example.json

# Capture and judge together only when both permissions are explicit.
dotnet run --project tools/AgenticRAGProd.Evaluation --no-build -- run --live \
  --base-url http://localhost:5080/ --suite smoke --judge llm --allow-model-calls
```

`--judge none` is the default. `--judge llm` requires `--allow-model-calls` before any answering or grading call. There is at most one grading call per eligible target and none for setup turns. The existing Semantic Kernel/Azure OpenAI libraries are reused, with SDK retries disabled. No new deployment is created. API failures, incomplete contracts and missing/failed setup are ineligible for grading. A missing legacy `answerKind` still allows a separate semantic assessment if the actual answer and evidence are otherwise available, but the kind check stays unassessed.

The fixed judge instruction surrounds a separate JSON data message containing the question, actual setup dialogue, reference facts/forbidden claims/answer, actual answer and numbered returned passages. All strings are treated as untrusted. Four strict status/reason objects are required: correctness/completeness, groundedness, relevance and citation support. Missing/extra fields, duplicate JSON properties, invalid statuses, truncation or provider errors produce `judge_error` and unassessed semantic checks. The raw judgment, prompt hash, evaluator version, deployment/model and available usage are recorded; absent usage stays unassessed. LLM judging is fallible and needs human audit. Reusing the answer deployment is allowed but is not independent validation. Matching declared model/deployment identifiers is flagged; unknown identifiers do not establish independence.

## Checks and reports

Transport/contract, actual answer kind, citation references, source location and semantic judgments are independent checks, following `Eval/RUBRIC.md`:

- `answer_kind` reads only the additive lowercase runtime `answerKind`. Historical `expectedAnswerKind`, punctuation and nonempty sources never establish an observed outcome.
- Citation-reference integrity catches missing required citations, invalid/zero/out-of-range references and duplicate source numbers. Clarification requires no citations and empty sources. Abstention may return sources without citations. If the actual kind is missing, existing numeric violations are still detected, but a clean partial check remains unassessed because response-specific rules cannot be completed.
- `source_locator_hit` compares normalized basenames, ignoring case, against alternative acceptable pages. A valid `context.pageStart/pageEnd` takes precedence; otherwise `pageNumber` is used, including legacy null context. Sections are informational. This is a coarse location check over final evidence, not factual support or retrieval recall.
- Keywords and values never produce a semantic pass. Correctness/completeness and relevance apply to all enabled cases. Groundedness also applies to abstention; citation support applies to answered cases. Clarification's groundedness and citation support, and abstention's citation support, are not applicable.
- Any assessed failure wins over missing assessments in a case verdict. Otherwise a required unassessed check keeps the case incomplete. Legitimate not-applicable checks are excluded. Disabled cases are skipped with their reasons.

Each run writes `run.json`, append-only `observations.jsonl`, append-only `scores.jsonl`, `summary.json` and `report.md`. Original captures are never overwritten. Offline replay copies observations into a new run while retaining the original capture run ID and provenance. Observation schema and score/report schema are version 1; dataset and rubric are v2; evaluator is 1.0.0 and judge is `rag-eval-judge-v1`. Question comparisons trim outer whitespace only; a changed question under the same ID is refused. Duplicate observation IDs are rejected: use one capture run per input.

Provenance includes input hashes, dataset/key/rubric hashes, local tool commit, original capture identifiers, client target latency, setup observations and evaluator identity. Missing service provenance is `unknown`; the local tool commit is not claimed as the deployed API commit. Optional `--settings` accepts only the non-secret fields in `provenance.example.json`. Replace those values with known deployment/corpus/prompt/configuration versions when available; the tool does not infer them or load credentials. Supplied identifiers are operator declarations, not independently discovered service state.

Reports show selected, enabled, matched, missing, disabled and error counts separately. `skipped` is the dataset-disabled count; `selectedSkipped` identifies any explicitly selected disabled cases. Check pass rates use pass+fail denominators; coverage uses assessed/eligible. Client target timing is distinct from internal stage timing; absent spans/token counts and production latency percentiles are not invented. A setup-blocked scenario has a captured record and an unattempted target.

Exit precedence is **2 > 1 > 3 > 0**: invalid input/configuration or tool/judge errors; assessed failures; otherwise incomplete assessment; complete required checks passing. Validation returns 0 for valid data without claiming answer quality. HTTP failures are assessed transport failures (1); judge/provider failures are tool diagnostics (2). Zero enabled cases cannot produce a quality pass (3).

Generated results may contain confidential answers and passages. Default `Eval/runs/` is ignored, and each generated run directory has its own `.gitignore`. These artifacts are separate from production logs; review them before explicitly sharing. The default semantic-free live commands normally return 3, even when every mechanical check passes.

## Why these choices

For an interview, explain the separation of observation from judgment first: copied expectations cannot be evidence about what the program did. Exposing the already computed application enum adds observability without changing retrieval, prompts or stored message fields. Recording once and grading offline makes failures reproducible and avoids repeated answer-service costs. A small judge interface and fake HTTP transport make the important tests deterministic and free of cloud dependencies. Distinct checks and explicit unassessed statuses make the report honest: good citation syntax or a matching page cannot prove a fact is correct or supported.

This small development suite does not establish generalized RAG accuracy. It has no holdout, raw candidate capture, qrels, OCR verification or independent human adjudication. Cases 005 and 018 remain disabled. Live corpus behavior, Azure configuration and evaluator calibration require separately authorized runs and review; no live answer or judge calls were made while implementing/testing this milestone.

## Implementation verification (2026-10-29)

The solution built with zero warnings/errors and all 224 local regression tests passed. Evaluation tests use temporary synthetic datasets, fake HTTP handlers and local judge delegates; no live Azure or judge calls were made. The supplied full-suite baseline replay returned exit 3: 16 enabled cases, four matching enabled observations, twelve missing, two dataset-disabled cases, zero complete passes/failures, and 16 incomplete verdicts. The four available responses pass transport and response-contract checks; three answered cases have coarse locator hits. All actual kinds are unavailable in that legacy capture, and semantic grading was disabled. Original input files and all seven pack manifest hashes were verified unchanged. These are mechanical verification results, not live-corpus accuracy findings.
