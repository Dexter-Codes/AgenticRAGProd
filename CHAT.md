# Persistent chat

Chat stores completed question/answer records in a local SQLite database. The database, table and history index are created automatically on first use. Immediate responses include numbered sources and their provenance; source passages are not persisted and there is no stored-source lookup route.

The service has no conversation-header, coordination, pending, failed, lease, attempt, request-registry or processing-state records. Azure AI Search retrieval and Semantic Kernel answer generation remain in place. Upload PDFs through `POST /api/documents`, then index them through `POST /api/documents/{documentId}/index?documentName=...` before asking chat about their contents. The configured Blob container and search index must already exist. Index provisioning, document-question and diagnostic endpoints remain removed. See the [upload → index → chat example](README.md#ingestion-and-retrieval) for the full workflow.

## API

Upload, indexing and chat are mapped directly in `Program.cs`. The chat route, `POST /api/chat`, is available only when both `Development` and `Chat:LocalSingleUserMode=true` apply; the upload and indexing routes preserve their existing availability independently of that switch. Startup rejects local mode outside Development. Swagger remains available in Development. Keep the listener on localhost. The dummy email is demonstration data, not authentication or authorization; no email is sent and the client supplies no email. Authentication and document authorization are still required before multi-user exposure.

```http
POST /api/chat
Content-Type: application/json

{"conversationId":null,"query":"What are the notification requirements?"}
```

A successful response is `200` with `conversationId`, `messageId`, `answer`, numbered `sources`, and additive `answerKind` (`answered`, `clarification`, or `insufficient_evidence`). The kind comes from the existing runtime `ChatAnswer.Kind`; it is response metadata and is not stored in SQLite. Supply that `conversationId` and a new question in subsequent requests. Each new question gets its own message ID. An unknown supplied conversation ID returns `404`; an empty GUID returns `400`. A conversation exists once it has at least one completed exchange.

Queries contain 1–2,000 characters. Every submission generates a fresh random message ID and processes the question again. Sending `conversationId:null` also generates a fresh random conversation ID. Repeating the same query creates another completed exchange.

The unused history endpoint, point-message lookup and older-page cursor code have been removed. There is no conversation-history HTTP endpoint. Follow-ups load bounded completed messages internally from SQLite, ordered deterministically by UTC completion timestamp and message ID. Use a SQLite client to inspect `chat_messages` when needed. Preserve an immediate response externally if its sources need to be reviewed later. `POST /api/chat` creates the first completed exchange itself.

Errors are `400` invalid input, `404` unknown conversation, `502` malformed query preparation/generation/citations, `504` timeout, or `503` other processing/persistence failure. Provider details, credentials and prompt contents are not included in error responses. After an uncertain failure, inspect saved chat messages when the conversation ID is known: the completed exchange may already have been saved. Submitting the question again creates another exchange, or another conversation when the conversation ID is null.

## Stored messages

The `chat_messages` table has a composite primary key `(conversationId, id)`. Conversation IDs use GUID `N` format (32 hexadecimal characters); API GUIDs may contain hyphens. Timestamps are normalized to UTC before storage. The table stores these six fields:

```json
{
  "id": "<messageId>",
  "conversationId": "<conversation GUID in N format>",
  "email": "test123@gmail.com",
  "query": "<original question, unchanged>",
  "answer": "<answer citing [1]>",
  "createdAtUtc": "2026-10-27T10:00:00.0000000+00:00"
}
```

The message ID is the SQLite `id`; there is no duplicate stored `messageId`, type discriminator, status, growing transcript, source reference or embedded passages. Citations remain in the saved answer, but their passages are not retained for later history access, including after restart. The prepared standalone question and `retrievalQuestion` are not persisted; `query` remains the original submitted question. For a new conversation, the service randomly chooses a numeric suffix for `test<number>@gmail.com`. Subsequent exchanges restore that email from existing completed records. There is no global counter or uniqueness service.

The immediate response contains sources with this shape:

```json
{
  "sources": [
    {
      "number": 1,
      "documentId": "<document GUID>",
      "chunkId": "<chunk ID>",
      "documentName": "<document name>",
      "pageNumber": 7,
      "passage": "<exact final passage supplied to answer generation>",
      "hasUnprocessedImages": false,
      "context": {
        "document": "<document name>",
        "pageStart": 7,
        "pageEnd": 8,
        "sectionPath": ["Notifications"],
        "warnings": ["<available extraction warning>"]
      }
    }
  ]
}
```

The existing `AnswerSource` contract preserves page ranges, section paths, extraction warnings and image indicators. `context` is derived from the exact passage prefix; legacy passages can have null context. The immediate response contains the final bounded source list used by the answer prompt, in the same numbered order, without discarded retrieval candidates. Clarifications and empty-retrieval answers return an empty `sources` array. No bundle is currently persisted for any answer.

## Processing and reliability

The service validates input, allocates or restores the conversation, assigns or restores email, loads bounded completed history, prepares the retrieval query, retrieves fresh chunks, packs bounded evidence, generates through Semantic Kernel, and persists the completed exchange. The configured server index is the permitted corpus; clients cannot select an index. Existing hybrid retrieval uses `CandidateCount` and `FinalContextCount`. No optional reranker is currently configured or added.

With `Chat:EnableQueryOptimization=true` (the default), one preparation call uses the existing Semantic Kernel model to resolve follow-up references and produce a concise retrieval query. The model must return exactly `question`, `retrievalQuestion`, and `clarification`: a resolved standalone question and a faithful retrieval query, or a clarification question when context cannot resolve the intent. The instructions preserve entities, numbers, dates, constraints and negation, and prohibit invented facts or answers. The retrieval query feeds both embeddings and the lexical part of hybrid search. The answer prompt retains the original question and the resolved standalone question, with the selected fresh passages as evidence. This stage improves query wording; it does not change cosine distance, neighbor counts, final context limits, ranking or evidence budgets.

The first question remains the original answer target. For follow-ups, dropping an explicit numeric term or double-quoted phrase while resolving the question fails preparation. If only the retrieval rewrite changes numeric terms or drops a quoted phrase, retrieval uses the complete standalone question instead, without another model call. These conservative literal checks do not establish semantic equivalence: entities, units and negation still depend on the model following its instructions. The preparation call shares the existing `AzureOpenAI:MaxOutputTokens` setting (default 500); lengthy output can be truncated, and malformed/truncated JSON is reported as a generation error rather than an insufficient-evidence answer.

At most `HistoryTurns` recent whole user/assistant pairs enter preparation, bounded by `HistoryTokens`. Prior assistant text is conversational context, not evidence. Historical source bundles are never loaded into model history. Clarification skips embedding, search and answer generation and returns no sources. Malformed preparation JSON fails with `502` before retrieval or persistence. Citation validation checks numbered reference integrity, not factual correctness or complete prompt-injection resistance.

An initial substantive chat question now uses one preparation model call and one answer model call, plus the existing embedding and search calls. Follow-ups combine resolution and optimization in one preparation call, so they still use two model calls in total. The additional initial model call adds latency and model usage. Setting `Chat:EnableQueryOptimization=false` bypasses preparation for initial questions and retains the existing follow-up resolver. Query optimization needs no new Azure resources, search schema changes, reindexing or persistence changes. Relevance improvements still need evaluation against representative corpus questions.

The persistence path performs one parameterized SQL `INSERT` and returns success after that write completes. Duplicate `(conversationId, id)` values fail without replacing existing data. History uses an index on `(conversationId, createdAtUtc DESC, id DESC)`, reads at most `limit + 1` rows, and returns the selected exchanges in chronological order with an earlier-history indicator.

Each operation opens its own connection. Write-ahead logging and a bounded busy timeout support concurrent requests. Parallel questions can still use the same history snapshot and finish in either order; the service does not serialize a conversation. Cancellation is checked before database work and between history rows. SQLite I/O is synchronous; the command timeout bounds lock waiting rather than providing asynchronous interruption of an active statement.

A failed or lost HTTP response can occur after a message was saved. Inspect the database before resubmitting: repeated submissions create fresh message IDs. No automatic cleanup, source persistence or request deduplication is implemented.

## SQLite configuration

```json
{
  "Sqlite": {
    "DatabasePath": "App_Data/chat.db",
    "CommandTimeoutSeconds": 30
  },
  "Chat": {
    "LocalSingleUserMode": false,
    "EnableQueryOptimization": true,
    "HistoryTurns": 8,
    "HistoryTokens": 2048,
    "EvidenceTokens": 6000,
    "RequestTimeoutSeconds": 180
  }
}
```

Relative database paths resolve against the API content root. With the documented local launch, the default is `src/AgenticRAGProd.Api/App_Data/chat.db`. Set `Sqlite__DatabasePath` to an absolute path to keep a stable database location across launch directories. The process needs write access to the parent directory for the database and its SQLite sidecar files. These files are ignored by Git. Keep backups of the database if the local conversations matter; stop the API before copying the database file and retain any existing WAL files.

No Cosmos account, credentials, container or indexing policy is used. Existing external `CosmosDb:*` settings are ignored. Old Cosmos messages are not copied automatically; a separate export would be needed to import previous conversations. Keep any existing export intact until an import has been validated.

SQLite replaces chat persistence only. Azure Blob Storage, Azure AI Search, and Azure OpenAI remain required for upload, indexing, retrieval and generation. Removing the Azure subscription does not make those services local. Configure working replacements separately before expecting the complete RAG workflow to run without Azure.

## Troubleshooting storage errors

A `503` titled `Chat storage configuration error` points to `Sqlite:DatabasePath`. Check directory permissions, disk space and whether another process holds the database locked beyond the configured timeout. Tables and indexes are created automatically. Provider exception messages and database paths are omitted from the response. Other processing failures retain the generic `503`; safe server logs include trace ID, request ID, exception type and available HTTP provider status.

To inspect saved messages using the SQLite CLI:

```sh
sqlite3 src/AgenticRAGProd.Api/App_Data/chat.db \
  'SELECT conversationId, id, query, answer, createdAtUtc FROM chat_messages ORDER BY createdAtUtc, id;'
```

## Local automated verification

Run the build and test commands in [README.md](README.md#build-and-test). Persistence tests use real temporary SQLite files and verify schema creation, reopening, immutable writes, concurrent stores, UTC normalization, conversation isolation, history bounds and safe storage failures. HTTP tests use SQLite with fake model/embedding/retrieval services. Adapter tests intercept the actual Azure OpenAI SDK transport to check embedding requests and Semantic Kernel answer requests without making network calls. Automated tests do not load app secrets or verify live model quality.

The [evaluation console tool](tools/AgenticRAGProd.Evaluation/README.md) validates the dataset, scores captures offline, and records explicitly enabled live runs. Offline scoring makes no model calls. Live evaluation still needs working model/search services and an indexed corpus. Expected answers and grading instructions stay outside production requests.
