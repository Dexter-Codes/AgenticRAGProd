# Persistent chat

Chat currently stores completed question/answer records in the existing `chat` container. Source-bundle storage is disabled because no RU budget is allocated for it. Immediate responses still include numbered sources and their provenance. No source-container reads or writes occur when sending a chat message, and there is no stored-source lookup route.

The service has no conversation-header, coordination, pending, failed, lease, attempt, request-registry or processing-state records. Azure AI Search retrieval and Semantic Kernel answer generation remain in place. Upload PDFs through `POST /api/documents`, then index them through `POST /api/documents/{documentId}/index?documentName=...` before asking chat about their contents. The configured Blob container and search index must already exist. Index provisioning, document-question and diagnostic endpoints remain removed. See the [upload → index → chat example](README.md#ingestion-and-retrieval) for the full workflow.

## API

Upload, indexing and chat are mapped directly in `Program.cs`. The chat route, `POST /api/chat`, is available only when both `Development` and `Chat:LocalSingleUserMode=true` apply; the upload and indexing routes preserve their existing availability independently of that switch. Startup rejects local mode outside Development. Swagger remains available in Development. Keep the listener on localhost. The dummy email is demonstration data, not authentication or authorization; no email is sent and the client supplies no email. Authentication and document authorization are still required before multi-user exposure.

```http
POST /api/chat
Content-Type: application/json

{"conversationId":null,"query":"What are the notification requirements?"}
```

A successful response is `200` with `conversationId`, `messageId`, `answer`, and numbered `sources`. Supply that `conversationId` and a new question in subsequent requests. Each new question gets its own message ID. An unknown supplied conversation ID returns `404`; an empty GUID returns `400`. A conversation exists once it has at least one completed exchange.

Queries contain 1–2,000 characters. Every submission generates a fresh random message ID and processes the question again. Sending `conversationId:null` also generates a fresh random conversation ID. Repeating the same query creates another completed exchange.

The unused history endpoint, point-message lookup and older-page cursor code have been removed. There is no conversation-history HTTP endpoint. Follow-ups load bounded completed messages internally from Cosmos DB, ordered deterministically by UTC completion timestamp and message ID. Use Cosmos Data Explorer to inspect saved records when needed. Preserve an immediate response externally if its sources need to be reviewed later. `POST /api/chat` creates the first completed exchange itself.

Errors are `400` invalid input, `404` unknown conversation, `502` malformed query preparation/generation/citations, `504` timeout, or `503` other processing/persistence failure. Provider details, credentials and prompt contents are not included in error responses. After an uncertain failure, inspect saved Cosmos messages when the conversation ID is known: the completed exchange may already have been saved. Submitting the question again creates another exchange, or another conversation when the conversation ID is null.

## Stored documents

The active `chat` container uses `/conversationId` as its partition key. Stored partition values are the conversation GUID in `N` format (32 hexadecimal characters), and SDK partition-key arguments use the same value. API GUIDs may appear with hyphens. Cosmos uses camel-case JSON. No source-container configuration is used by the current application.

One completed record in `chat`:

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

The message ID is the Cosmos `id`; there is no duplicate stored `messageId`, type discriminator, status, growing transcript, source reference or embedded passages. Citations remain in the saved answer, but their passages are not retained for later history access, including after restart. The prepared standalone question and `retrievalQuestion` are not persisted; `query` remains the original submitted question. For a new conversation, the service randomly chooses a numeric suffix for `test<number>@gmail.com`. Subsequent exchanges restore that email from existing completed records. There is no global counter or uniqueness service.

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

An initial substantive chat question now uses one preparation model call and one answer model call, plus the existing embedding and search calls. Follow-ups combine resolution and optimization in one preparation call, so they still use two model calls in total. The additional initial model call adds latency and model usage. Setting `Chat:EnableQueryOptimization=false` bypasses preparation for initial questions and retains the existing follow-up resolver. Query optimization needs no new Azure resources, search schema changes, reindexing or Cosmos updates. Relevance improvements still need evaluation against representative corpus questions.

The active persistence path performs one immutable create in the chat container and returns success after that write is confirmed. Messages have no source reference, and the store does not write source documents. Saving a new answer requires no sources container or additional allocated throughput.

A failed message write does not create a source bundle. An uncertain create response may still mean the message committed; inspect saved Cosmos messages when the conversation ID is known. Submitting a question again performs new model, embedding and search calls and creates new identifiers, so a lost response can lead to another saved exchange or conversation. Existing records are never upserted or replaced, and no automatic cleanup is performed.

Restoring source persistence requires a deliberate code and contract change along with approved capacity. Cosmos transactional batches cannot span two containers. A future design must account for partial or uncertain writes and durable source references; the current API does not promise source recovery after a restart.

Parallel questions may use the same prior-history snapshot and complete in either order. This design does not serialize a conversation. Time/ID ordering is deterministic, not a causal or globally allocated sequence. Internal history reads are not a snapshot across requests, and an in-flight question can complete after another question has loaded its history.

This store assumes the account's configured consistency permits the expected completed reads. For older source bundles, do not configure TTL or deletion independently of the messages that reference them.

## Troubleshooting storage errors

A `503` titled `Chat storage configuration error` includes safe setup guidance. A missing chat database or container during a write names the relevant configuration setting. A missing history composite index identifies `/createdAtUtc DESC` and `/id DESC`. Pausing source writes does not resolve this separate history-index requirement: follow-ups still require it for their internal history reads. The last read-only diagnosis found that index missing in the configured chat container. Follow the manual setup below; the API does not create containers or change indexing policies.

Other processing failures retain the generic `503`. Server logs include the same activity trace ID as the response, the local request ID, exception type and available provider status. They omit raw provider messages, credentials and document contents. Restart a running API after rebuilding to load these diagnostics. Inspect saved Cosmos messages before resubmitting a failed request when its conversation ID is known, because an uncertain write may already have completed.

## Manual container setup

No Azure provisioning, data deletion, index-policy change or partition-key change is performed by the application or automated tests. Review resource/cost choices before these manual setup steps.

1. In the existing Cosmos NoSQL account/database, inspect the existing `chat` container and confirm its partition key is `/conversationId`. Do not change an existing partition key. If it differs, explicitly plan a replacement container and migration, then configure its name.
2. Source storage is paused: no sources container or source-container configuration is required. Restoring source persistence later requires a deliberate code, contract and capacity change; the current store resolves only the chat container.
3. For follow-ups and deterministic history, ensure the existing chat indexing policy includes this composite index even while source storage is paused. Merge it into the existing policy, preserving unrelated indexes; wait for any index transformation to complete before testing.

```json
{
  "compositeIndexes": [
    [
      { "path": "/createdAtUtc", "order": "descending" },
      { "path": "/id", "order": "descending" }
    ]
  ]
}
```

4. Review and migrate or remove identifiable legacy records as described below before enabling the new history queries.
5. Keep the existing Cosmos connection string and other Azure credentials in User Secrets or environment configuration. Non-secret settings are:

```json
{
  "CosmosDb": {
    "DatabaseName": "rag-demo",
    "ContainerName": "chat",
    "RequestTimeoutSeconds": 30
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

Development configuration enables local mode. Only the chat container is configured or resolved; obsolete external `CosmosDb:SourcesContainerName` settings are ignored and can be removed. `EnableQueryOptimization` defaults to `true` when omitted; set it to `false` in configuration to use the previous query preparation behavior. Remove obsolete `Chat:LeaseSeconds` and `Chat:TurnTimeoutSeconds` settings from external configuration; the operation timeout is now `Chat:RequestTimeoutSeconds`. Search deployment/index names and existing credentials remain server configuration.

## Targeted legacy migration or cleanup

The new history query reads ordinary records in the conversation partition and does not filter artificial record categories. **Do not run it against an unmigrated partition containing old metadata/turn documents.** No legacy Azure data is automatically purged.

Stop all old and new API writers, drain in-flight requests, and export a backup before modifying data. Use Data Explorer read-only queries to identify the known previous schema, first scoped to an explicitly selected conversation partition:

```sql
SELECT c.id, c.conversationId, c.type, c.status, c.message, c.result
FROM c
WHERE c.conversationId = '<selected conversation GUID in N format>'
  AND ((c.id = 'conversation' AND c.type = 'conversation')
       OR (STARTSWITH(c.id, 'turn-') AND c.type = 'turn'))
```

Inspect the results and export full JSON for each selected record. To retain completed history in the current message-only format, map old `message` to `query`, `result.answer` to `answer`, normalize UTC timestamps and choose one dummy email per conversation. Omit source references from the current shape. Keep any old `result.sources` in the reviewed export; no source migration is required or run while storage is paused. Restoring source recovery requires a separate code and data migration. The old metadata-only conversations and pending/failed records have no completed answer to migrate. Use a reviewed staging/replacement container when a legacy message ID would collide with the desired target ID.

After the retained data is verified, point-delete only individually reviewed legacy IDs using their `id` and `conversationId` partition values, preferably conditional on the exported ETag. Never use an unrestricted partition/container purge or delete new ordinary records. Re-run the identifying query and inspect the ordinary history before restarting writers. Unknown schemas require separate review; do not infer they are safe to delete.

Any older orphan-bundle cleanup is also manual: stop writers and drain in-flight/uncertain operations first; for each candidate bundle, read its conversation's completed messages and verify that none references the bundle ID. Confirm there is no unresolved commit or in-flight attempt that could still reference it before an individually reviewed deletion. An absent read during live or ambiguous writes is not safe proof that a bundle is unreferenced. There is no TTL-based cleanup or automatic repair.

## Local automated verification

Verified locally on 2026-10-27 after the source cleanup: solution build succeeded with zero warnings and errors; all 152 tests passed, with none skipped. Route and Swagger checks cover upload, document indexing and gated chat. A multipart upload → index → chat regression exercises the production handlers and indexing service with local service doubles, including newly indexed evidence in the chat response. Invalid upload/indexing inputs are rejected before storage or embedding work; missing indexing parameters return `400`. No live Azure requests, provisioning or reindexing were performed.

```sh
dotnet restore AgenticRAGProd.slnx -p:NuGetAudit=false
dotnet build AgenticRAGProd.slnx --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/AgenticRAGProd.Tests/AgenticRAGProd.Tests.csproj --no-build --no-restore -m:1 -nodeReuse:false -p:NuGetAudit=false
```

The suite runs production persistence against mocked Cosmos containers and fake model/embedding/retrieval services. HTTP tests execute actual Minimal API handlers in memory, including query optimization, preservation of the original stored question, host recreation, restored history and immediate response sources. A strict Cosmos-client test verifies that the store resolves only the configured chat container; the message-container mock supports only completed creates and bounded history queries. No test loads app secrets or makes paid Azure calls. Existing ingestion/retrieval tests remain. Local mocks establish mechanical behavior; they do not verify the live account's containers, indexing policy, consistency, network failures, real-corpus relevance or answer quality.

## Optional live round trip

These manual requests use configured real services and may incur Azure charges. They are not executed automatically. Complete container setup and legacy-data review first. Start the API on localhost:

```sh
DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AgenticRAGProd.Api --no-launch-profile --urls http://localhost:5080
```

In another zsh terminal, use a concrete question about a document already indexed in the configured corpus. Keep this terminal open across the API restart:

```sh
chat_check_dir=$(mktemp -d /tmp/rag-chat-check.XXXXXX)
read -r 'chat_question?Enter a question about an indexed document: '
python3 -c 'import json,sys; print(json.dumps({"conversationId":None,"query":sys.argv[1]}))' "$chat_question" > "$chat_check_dir/first.json"
curl --fail-with-body -sS -X POST http://localhost:5080/api/chat \
  -H 'Content-Type: application/json' \
  --data-binary @"$chat_check_dir/first.json" > "$chat_check_dir/first-result.json"
python3 -m json.tool "$chat_check_dir/first-result.json"
chat_conversation_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["conversationId"])' "$chat_check_dir/first-result.json")
read -r 'chat_followup?Enter a follow-up question: '
python3 -c 'import json,sys; print(json.dumps({"conversationId":sys.argv[1],"query":sys.argv[2]}))' "$chat_conversation_id" "$chat_followup" > "$chat_check_dir/followup.json"
curl --fail-with-body -sS -X POST http://localhost:5080/api/chat \
  -H 'Content-Type: application/json' \
  --data-binary @"$chat_check_dir/followup.json" > "$chat_check_dir/followup-result.json"
python3 -m json.tool "$chat_check_dir/followup-result.json"
```

Stop and restart the API with the same command, then submit another follow-up using the saved conversation ID. A reference to the previous topic exercises restoration of completed history from Cosmos DB:

```sh
read -r 'chat_restart_followup?Enter a follow-up referring to the previous topic: '
python3 -c 'import json,sys; print(json.dumps({"conversationId":sys.argv[1],"query":sys.argv[2]}))' "$chat_conversation_id" "$chat_restart_followup" > "$chat_check_dir/restart-followup.json"
curl --fail-with-body -sS -X POST http://localhost:5080/api/chat \
  -H 'Content-Type: application/json' \
  --data-binary @"$chat_check_dir/restart-followup.json" > "$chat_check_dir/restart-followup-result.json"
python3 -m json.tool "$chat_check_dir/restart-followup-result.json"
```

Inspect saved Cosmos messages in Data Explorer to verify distinct message IDs, reused demo email and the original submitted questions. The immediate response files retain numbered sources and available citation/page/section/warning details; these passages are not durably stored during the pause. A clarification should return an empty sources array. Sending the same question again generates another message; sending a null conversation ID creates another conversation.
