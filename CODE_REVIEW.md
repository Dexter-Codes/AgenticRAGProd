> Persistence update (2026-11-02): SQLite has replaced Cosmos DB. The Cosmos findings below describe the earlier implementation; current setup is in [CHAT.md](CHAT.md#sqlite-configuration).

# Code review and cleanup — 2026-10-27

Reviewed the runtime paths for PDF upload, document indexing, hybrid corpus retrieval, query preparation, answer generation and Cosmos message persistence. Chatbot memory changes remain deferred. The three supported endpoints and their JSON/form contracts are preserved.

## Findings addressed

| Priority | Finding | Change |
| --- | --- | --- |
| P1 | The IDE reports conflicting accessibility for the explicit and generated partial `Program` declarations. | Changed the explicit declaration to `public partial class Program`. The command-line solution already built before this edit; the reported IDE diagnostic identified the declaration to correct. |
| P2 | Disabled source persistence still resolves a second Cosmos container and validates its name, so an obsolete source setting can reject otherwise valid message-only configuration. | The store now resolves only the configured chat container. Removed the source option, validation, unused field, DTO, validation helper and transient source-bundle allocation. A strict client test checks the single-container lookup. No existing Azure container or document was deleted. |
| P2 | Removed document-question endpoints left an entire alternative question/retrieval path registered without a runtime caller. | Removed `DocumentQuestionService`, `DocumentRetrievalService`, `IChunkRetriever`, `DocumentAnswer` and the optional document-filter branch in the chat retriever. Kept source attribution through the shared `AnswerSource` contract. |
| P2 | Removed diagnostic provisioning left an unused index provisioner and `SearchIndexClient` registration. | Removed both. Upload and document indexing continue to use the existing configured Blob container and search index; required search fields and the cosine profile are documented in README. |
| P3 | Point-message lookup, the history-service wrapper and older-page cursor handling have no remaining API consumer. | Reduced the store contract to completed-message saves and bounded recent-history reads. A boolean records whether earlier messages were omitted. Partitioning, UTC/ID ordering, the look-ahead row and empty SDK-page handling remain. |
| P3 | Source-container test scaffolding included an always-empty property, and parsed pages carried an always-empty removed-margin field. | Removed those artifacts and their redundant assertions. Persistence tests continue to cover immutable writes, conflicts, ambiguous commits, validation and history isolation. |
| P3 | Source metadata was declared in the removed document-answer file. | Moved `AnswerSource` into its own conversation contract file. Citation numbers, document/chunk IDs, passages, page ranges and warnings retain their response shape. |

All model instructions remain in `PromptConstants`; prompt wording is unchanged. Obsolete dependency registrations and imports were removed. `Newtonsoft.Json` remains because the installed Cosmos SDK's build targets explicitly require it, including when the application selects System.Text.Json for item serialization.

## Regression coverage

- Multipart PDF upload → real indexing service/chunker → chat with newly indexed evidence, using local service doubles.
- File size/type checks, invalid indexing parameters, missing query parameters and generated OpenAPI contracts.
- Chat's Development/local-mode gate, input validation, provider-error privacy and 502/503/504 responses.
- Original-question persistence, query optimization, follow-up resolution and fresh-source grounding.
- Cosmos single-container construction, immutable messages, failed/ambiguous writes, concurrent submissions, UTC normalization and bounded partition-scoped history.
- Hybrid text/vector request shape, configured candidate/context counts, source provenance and invalid-vector rejection.
- A new intercepted-SDK regression verifies that reindex cleanup still filters by document ID and deletes only obsolete chunks. Removing document filtering from chat does not remove this necessary indexing filter.
- Existing PDF parsing, table/provenance extraction and token-budget tests remain.

## Existing limits retained

- Upload and indexing preserve their original availability and have no authentication. Chat is gated to local Development mode. Authentication and document authorization remain necessary before multi-user deployment.
- Reindex replacement is not atomic, and concurrent indexing of the same document can interleave uploads and cleanup. This cleanup does not add indexing coordination or change chunk identifiers.
- Upload validates extension and size; malformed PDF bytes can be stored and then rejected during indexing. Parsing and indexing are synchronous, with the existing 100-page limit.
- Citation checks establish reference integrity, not factual support. Query literal checks do not prove semantic equivalence. Real-corpus retrieval and answer quality still require evaluation.
- Query preparation shares the existing output-token setting with answer generation. Long preparation output can be truncated; malformed output remains an explicit generation error.
- Cosmos still needs its existing conversation partition key and history composite index. No Azure account, indexing policy or live-service behavior was changed or verified in this review.

## Verification

Solution build: zero warnings and errors. All 152 local tests passed, with none skipped. A read-only compiler-style check for unused imports/private members also completed successfully. Tests use fake services, strict Cosmos mocks and intercepted Azure SDK transports; no paid Azure calls, provisioning, migration or reindexing were performed.
