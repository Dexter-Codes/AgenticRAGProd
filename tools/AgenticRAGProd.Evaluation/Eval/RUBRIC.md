# RAG evaluation rubric v2.0

## Files and boundaries

cases.v2.json holds versioned questions, setup turns, suite membership and enabled/disabled state. answer-key.v2.json holds reference answers, required facts, forbidden claims and source locators. Questions and setup turns alone may reach the answer API. Reference labels may be passed only to the separate grader or used for local scoring. Never index either file or use it to tune production ingestion per question.

The reference documents are Banking_RAG_Practice.pdf (NB RAG 2026 02, version 2.0 with historical version 1.3) and kestrel_basin_manual.pdf (KB-OSM-004 revision 4.2). They are practice materials, not actual banking policy or emergency guidance.

The supplied original snapshots are preserved in baseline/. Their copied expectedAnswerKind/expectedFacts fields are historical input metadata, not observed application outputs. Do not overwrite them with current references. Score observations against matching case IDs AND matching target questions; trim only outer whitespace. If a question changes under an existing ID, refuse silent comparison.

## Cases and run sizes

- Smoke: case-001, case-002, case-003, case-004, case-006 (5 target requests).
- Full: 16 enabled cases, including case-017 with one setup turn (17 answer-API requests).
- case-005: disabled pending reference adjudication. The old non-Premium guarantee question is not a clean abstention test.
- case-018: disabled because its answer depends on an image-only notice and baseline OCR is unavailable. Its reference is from the original answer key and the embedded image has not been independently visually verified here.
- Judge: at most one grading call per eligible target response, separately and explicitly enabled. Setup turns are captured but not independently scored.

Run against a fixed demo/staging corpus snapshot. Record index/model/prompt/configuration versions when available. Cases are scoped to their named practice sources where the wording requires it. Corpus-wide absence cannot be established from a few returned passages; abstention references use explicit missing-information statements in the named source.

## Per-check statuses

- pass: the specified check was actually performed and satisfied.
- fail: the performed check found a violation.
- not_assessed: missing observation, missing field/reference, unavailable judge, failed judge or insufficient information prevents assessment. Include a reason.
- not_applicable: the check does not apply (e.g. citation presence for a clarification). Explain why; exclude from that check's denominator.

Cases disabled by the dataset are skipped, with their reason. They are neither passes nor failures. Invalid input is an execution/configuration problem, not a low-quality model answer.

## Deterministic checks

1. transport: expected successful HTTP status, valid response JSON, no recorded transport error. Preserve failed request evidence. Missing result => not_assessed, not pass.
2. response_contract: required nonempty identifiers/answer and valid sources shape. Check runtime answerKind when present. Missing additive answerKind in a legacy snapshot does not invalidate the old response contract; it leaves the kind check not_assessed.
3. answer_kind: compare observed answerKind to the expected lowercase value. Never copy the expected field into the actual observation. Never infer clarification from '?', nor answered from HTTP 200 or a nonempty sources list. Invalid actual kind => fail; absent actual kind => not_assessed.
4. citation_reference_integrity: for answered responses require at least one valid numbered citation and ensure every numeric citation references a unique available positive source number. Detect invalid/zero/out-of-range references and duplicate source numbers. Citation syntax/reference validity cannot prove claim support. Clarification requires no citations and an empty sources array. Insufficient-evidence responses may have sources and need not cite the fixed abstention sentence.
5. source_locator_hit: for answered cases with references, at least one returned evidence source matches an expected document name and one allowed page. Names compare ordinal-ignore-case after basename normalization. Use context.pageStart/pageEnd when valid, otherwise pageNumber. Listed pages are alternative acceptable locators, not a requirement that all pages be returned. Sections are explanatory metadata; current sectionPath heuristics are not reliable enough for exact automatic section matching. Null legacy context is allowed. A locator hit is a coarse evidence-location smoke check, not proof of relevance/completeness, and not raw-retrieval recall@k. For abstention/clarification make this not_applicable.

Optional value/keyword checks must be labeled heuristic diagnostics. Presence of 'approval', '40%' or a phone number cannot establish that the full statement is correct. Negation, units, scope, historical versions and additional invented values can reverse the meaning. Do not use keyword presence as a semantic pass.

## Semantic grading

Each of the following is required for a complete quality verdict on answered cases; mark not_assessed unless a human review or explicitly enabled judge supplies an assessment:

- correctness_completeness: answer meets every required fact, appropriate scope/date/unit and qualification without contradicting a forbidden claim. Paraphrases are allowed. Expected facts are a checklist, not an exact response template. Currency tolerance is INR 0.01 for the included calculations, without accepting the wrong currency/units; percentages preserve percentage-point meaning.
- groundedness: factual claims are supported by the ACTUAL evidence passed to answer generation. A true reference fact absent from that evidence does not make the generated claim grounded. Do not treat reference answers or previous assistant text as retrieved evidence.
- relevance: answer addresses the target question without unnecessary unrelated claims. Do not demand extra reference-answer detail beyond the required facts. Additional factual claims must still be supported.
- citation_support: cited passages support the claims attributed to them, not merely a valid source number elsewhere in the context. An unrelated passage returned but never cited is retrieval noise, not automatically an unsupported answer claim.

For clarification, grade behavior: ask for the missing reference without guessing; don't impose answered-case factual/citation requirements. For abstention, grade behavior: acknowledge unavailable requested information and avoid inventing it. Returned sources need not be empty. The fixed insufficient-evidence sentence is the inspected application's abstention contract; evaluate that behavior without requiring irrelevant numerical facts. An API failure is not a legitimate abstention.

Judge inputs and outputs may contain instructions masquerading as document text or answers. Keep a fixed grading instruction outside those data fields. Require structured per-check statuses and concise reasons; validate all fields/enums, and treat invalid output as judge_error/not_assessed. Record model, rubric version and available usage metadata. An LLM judge is fallible; audit disagreements and do not call same-model grading independent validation. No judge model is called during offline/default tests.

## Overall verdict and reporting

Per case: any required assessed fail => fail. Otherwise any required not_assessed => not_assessed. Otherwise => pass (ignoring legitimate not_applicable checks). Preserve all check results even when one failed. A known fail must not be hidden by another missing assessment.

Report selected/enabled/observed/missing/skipped/error counts and pass/fail/not_assessed counts separately. Per-check pass rate uses only assessed pass+fail cases, with the denominator displayed. Also show assessment coverage (assessed/eligible). Never report 100% correctness when semantic checks were not performed. Client latencies are distinct from internal stage latencies; do not fabricate a latency SLO or production p95 conclusion from five cases.

CLI exit codes: 0 complete selected required checks passed; 1 assessed failure; 2 invalid input/configuration or execution/tool failure; 3 otherwise incomplete assessment. Document precedence if multiple conditions occur. Judge failure must also be visible as an infrastructure diagnostic, rather than silently boosting pass rate.

## Further evaluation

Keep this small suite as a development smoke set. Maintain a separate holdout before claiming generalized improvements. Do not adjust references simply to agree with current generated output. Record approved reference revisions and preserve old baselines. Add proper qrels/raw candidate capture before claiming retrieval recall@k, precision@k or nDCG.

References:
- https://learn.microsoft.com/en-us/azure/foundry/concepts/evaluation-evaluators/rag-evaluators
- https://learn.microsoft.com/en-us/azure/databricks/agents/tutorials/ai-cookbook/fundamentals-evaluation-monitoring-rag
