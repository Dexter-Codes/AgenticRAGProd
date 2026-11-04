# AgenticRAGProd automated evaluation starter pack

This pack specifies the next coding milestone; it does not contain an implemented evaluation harness. Feed CODEX_PROMPT.md to Codex in your existing repository after copying the pack under tools/AgenticRAGProd.Evaluation/Eval/.

## Included

- cases.v2.json: 18 versioned cases, 16 enabled; five-case smoke suite and one conversation-memory scenario.
- answer-key.v2.json: explicit answer kinds, reference answers, fact checklists, forbidden claims and source locators. These references were checked against original source text and existing answer keys; the disabled OCR case is explicitly unverified visually.
- RUBRIC.md: deterministic vs semantic checks, missing-data handling and reporting requirements.
- CODEX_PROMPT.md: implementation task for the current repository.
- baseline/: byte-for-byte copies of the uploaded original cases and run 048194885e1f4a05afbad6dd24281090. They are observations, not answer keys.

Keep all evaluation/reference/results files outside ingestion. Only questions and actual setup turns go to the answer API. Expected facts can be provided to an explicitly separate grading model, never to the generator.

## Original reference keys

The original banking key has 25 questions, and the original Kestrel key has 31. This smaller suite selects and adapts verified cases for the current text-only implementation, adds the actual ambiguity case and a follow-up scenario, and preserves the disputed non-Premium case as disabled. It does not relabel that case to fit the observed response.

Banking_RAG_Practice.pdf page 7 says undated questions use version 2.0 explicitly. Dated 31 October and 1 November questions test historical/current boundaries. Kestrel sections were cross-checked against the five-page original PDF. Physical PDF page numbers are used; section labels such as 5.5 are not page numbers.

The card-replacement question is intentionally scoped to the banking handbook; page 6 explicitly says that charge is unspecified. No artificial product/source filters need to be added to production retrieval to pass these cases.

## Expected workflow after Codex implements the prompt

Run validation and offline scoring first. Legacy snapshots have no actual answerKind field, so that check must be not_assessed until a fresh response records it. Offline mechanical checks alone must not yield a complete semantic pass.

Next, manually run a five-case live smoke suite. Live API requests use configured Azure services and save completed exchanges. Optional LLM judging costs additional model calls; it stays off unless explicitly enabled. The full enabled suite uses 17 answer-API requests (16 targets plus one setup). Keep generated result artifacts out of git by default and in controlled evaluation storage.

No application code, Azure resources or original files were changed by creating this pack. JSON consistency, reference joins, enabled-case labels, arithmetic and baseline-copy integrity were checked. The Codex implementation and all future live evaluations still need to be run in your repository.
