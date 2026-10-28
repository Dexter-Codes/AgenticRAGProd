# AgenticRAGProd

.NET 10 Minimal API for PDF upload, document indexing and persistent corpus chat. Uses Azure Blob Storage, Azure OpenAI, Azure AI Search, Semantic Kernel and Cosmos DB.

## Run locally

Configure the existing Azure endpoints, deployment/index names and credentials through User Secrets or environment variables. Keep credentials out of source files. Chat currently persists completed exchanges only in the existing `chat` container. Source storage is temporarily disabled because no RU budget is allocated for it; numbered sources remain in each immediate response. The existing chat history indexing policy is still required, as described in [CHAT.md](CHAT.md#manual-container-setup). The application does not provision resources.

```sh
dotnet restore AgenticRAGProd.slnx -p:NuGetAudit=false
DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AgenticRAGProd.Api --no-launch-profile --urls http://localhost:5080
```

Open [Swagger UI](http://localhost:5080/swagger) or its [OpenAPI document](http://localhost:5080/swagger/v1/swagger.json). Swagger is available in Development. Upload, indexing and chat use the configured Azure services and may incur charges; browsing Swagger does not call the models.

Chat is available only in Development with `Chat:LocalSingleUserMode=true`. Keep this local demonstration on localhost; it has no authentication or document authorization. `POST /api/chat` accepts `{"conversationId":null,"query":"..."}` for a new conversation. Subsequent requests supply the returned conversation ID. Every submission creates a new message; sending a null conversation ID creates a new conversation.

The three workflow endpoints are mapped directly in `Program.cs`:

| Endpoint | Purpose |
| --- | --- |
| `POST /api/documents` | Upload a non-empty PDF of up to 10 MB using multipart field `file`; returns its `documentId`, `fileName`, `sizeBytes` and upload status. |
| `POST /api/documents/{documentId}/index?documentName=...` | Parse, chunk, embed and index an uploaded document; `documentName` must contain 1–255 characters. |
| `POST /api/chat` | Ask questions across the indexed corpus and continue a conversation. |

Upload and indexing are independent of the chat local-mode switch, preserving their existing availability. Index provisioning, document questions, diagnostics, health and history endpoints remain removed. The configured Blob container and Azure AI Search index must already exist; the API does not provision them. Upload does not automatically index the PDF: complete indexing before asking chat about it.

[CHAT.md](CHAT.md) documents the chat contract, query optimization, temporary source-storage pause, persisted history, container setup, legacy migration and reliability limits.

## Debug in VS Code

Open this repository folder in VS Code with the recommended **C#** and **C# Dev Kit** extensions. In **Run and Debug**, select **AgenticRAGProd.Api: Debug** and press **F5**. The launch builds the API in Debug mode, uses the API project directory and Development environment, and opens [Swagger](http://localhost:5080/swagger) when the server is ready. Existing User Secrets remain in use; keep credentials out of `.vscode` files.

Useful breakpoint locations are `ChatService.SendAsync` for request validation/history, `ChatAnswerService.AnswerAsync` for retrieval and generation, and `CosmosConversationStore.SaveAsync` for the message write. **F9** toggles a breakpoint, **F10** steps over, **F11** steps into, **F5** continues and **Shift+F5** stops debugging. The Debug Console shows application output.

If port `5080` is already in use, stop your earlier API process or select **AgenticRAGProd.Api: Attach** and choose the existing `AgenticRAGProd.Api` process. To inspect the original exception behind a `503`, optionally enable breaking on thrown CLR exceptions in **Run and Debug → Breakpoints**; the endpoint filter otherwise catches that exception and returns a safe response.

Debugging retains the source-storage pause and the existing Cosmos history-index requirement. Sending a real chat question still invokes the configured Azure services; opening Swagger does not invoke the models.

Verified on 2026-10-27: the F5 build and managed launch succeeded, the breakpoint in `ChatService.SendAsync` paused with request locals and the managed call stack visible, and execution resumed successfully. A blank-query request returned the expected `400` before embedding, retrieval, generation or chat storage, and the Swagger document returned `200`. No model calls or chat writes were made for this debugger check.

## Ingestion and retrieval

Upload a PDF, use the returned ID to index it, then ask a question about its contents. These optional manual requests call real Azure services; they are not run by the automated checks:

```sh
rag_workflow_dir=$(mktemp -d /tmp/rag-workflow.XXXXXX)
read -r 'rag_pdf?Enter the PDF path: '
rag_document_name=$(basename "$rag_pdf")
curl --fail-with-body -sS -X POST http://localhost:5080/api/documents \
  -F "file=@$rag_pdf;type=application/pdf" > "$rag_workflow_dir/upload.json"
python3 -m json.tool "$rag_workflow_dir/upload.json"
rag_document_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["documentId"])' "$rag_workflow_dir/upload.json")
rag_index_query=$(python3 -c 'import sys,urllib.parse; print(urllib.parse.urlencode({"documentName":sys.argv[1]}))' "$rag_document_name")
curl --fail-with-body -sS -X POST \
  "http://localhost:5080/api/documents/$rag_document_id/index?$rag_index_query" \
  > "$rag_workflow_dir/index.json"
python3 -m json.tool "$rag_workflow_dir/index.json"
read -r 'rag_question?Enter a question about the indexed PDF: '
python3 -c 'import json,sys; print(json.dumps({"conversationId":None,"query":sys.argv[1]}))' "$rag_question" > "$rag_workflow_dir/chat.json"
curl --fail-with-body -sS -X POST http://localhost:5080/api/chat \
  -H 'Content-Type: application/json' \
  --data-binary @"$rag_workflow_dir/chat.json"
```

Confirm each request succeeds before continuing. The indexing response includes `documentId`, `pageCount`, `indexedChunks`, `maximumChunkTokens`, `pagesWithUnprocessedImages` and extraction warnings. Chat searches the configured corpus; it does not require a document ID.

- The indexing service supports PDFs up to 100 pages. Parsing preserves ordered prose, headings, reliable tables, original page text and words.
- Chunking preserves section paths across pages and splits oversized prose down to Unicode scalar boundaries. Bounded prose overlap stays within a section; reliable table groups repeat their header without prose overlap.
- A serialized `RAG context` prefix carries document name, page range, section path and extraction warnings. Prefixes and repeated headers count toward the token budget. Oversized metadata fails before embedding rather than being silently truncated.
- Ambiguous tables and repeated margins remain searchable text with warnings. Image-only pages and unprocessed images produce warnings; OCR is not implemented. No missing table values or relationships are inferred.
- Corpus chat uses hybrid BM25/vector search across the server-configured index, retaining the existing HNSW cosine configuration and Azure hybrid score order; reranking is not configured. Document IDs remain in returned source provenance and in the indexing service's obsolete-chunk cleanup filter.
- Chat prepares a retrieval query through the existing Semantic Kernel model before embedding and hybrid search, with literal checks for explicit numbers and quoted phrases. Follow-up reference resolution and query optimization share one preparation call. The original question remains in history, and fresh passages ground the answer. This adds one model call for an initial question; follow-ups still use one preparation call and one answer call. Set `Chat:EnableQueryOptimization=false` to retain the previous first-question flow and follow-up-only resolution.
- Answer sources include document/chunk IDs, page numbers, exact passages, image indicators and available context. Legacy passages without a context prefix retain their page number and expose null context.

## Configuration

The following optional settings retain the defaults shown:

```json
{
  "Chunking": {
    "MaxTokens": 512,
    "OverlapTokens": 64
  },
  "AzureSearch": {
    "CandidateCount": 20,
    "FinalContextCount": 5
  },
  "Chat": {
    "EnableQueryOptimization": true
  }
}
```

`MaxTokens` is 128–2048. `OverlapTokens` is 0–256 and must be less than half the chunk budget. Search counts are 1–1000, with `FinalContextCount` no greater than `CandidateCount`. Candidate count controls vector neighbors and requested hybrid results; final context count limits returned passages. These settings do not change retrieval scope or cap Azure's internal lexical scoring work. Query optimization uses the existing model deployment and search index; it requires no new Azure resource, index migration or reindexing. Chat history/evidence budgets and Cosmos settings are described in [CHAT.md](CHAT.md#manual-container-setup).

## Existing search indexes

The existing Azure AI Search index must contain `id` (key), filterable `documentId`, `documentName`, `pageNumber`, searchable `content`, `hasUnprocessedImages`, and `contentVector` with the configured embedding dimensions and HNSW cosine profile. Index creation is an external setup step; there is no unused provisioning service in this API.

No Azure AI Search schema migration is required for passage context: it is stored and embedded in the existing `content` field. Vector dimensions and metric are unchanged. Cosmos container/index setup is separate and documented in [CHAT.md](CHAT.md#manual-container-setup).

To apply the current extraction/chunking behavior to older documents, call `POST /api/documents/{documentId}/index?documentName=...` with each document's existing ID and name. Reindexing incurs embedding/indexing costs and is never automatic. The indexing service validates all chunks, embeds and uploads replacements, then removes obsolete chunk IDs for that document. Replacement is not atomic. Chunk IDs use document-wide ordinals; refresh external chunk-ID references and result caches after reindexing.

## Build and test

```sh
dotnet restore AgenticRAGProd.slnx -p:NuGetAudit=false
dotnet build AgenticRAGProd.slnx --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/AgenticRAGProd.Tests/AgenticRAGProd.Tests.csproj --no-build --no-restore -m:1 -nodeReuse:false -p:NuGetAudit=false
```

Tests use synthetic content, in-memory PDFs, the real tokenizer, mocked Cosmos containers, fake model/embedding services and intercepted Azure SDK transports. They cover parsing, chunking, indexing, retrieval-query preparation, citations, message persistence without source-container calls, immutable writes and API gating. They do not load app secrets or make paid Azure calls. VSTest requires its local communication socket to be permitted. See [CHAT.md](CHAT.md#local-automated-verification) for persistence verification details.

The `AgenticRAGProd` rename was verified on 2026-10-28: the solution built with zero warnings and errors, and all 152 tests passed. Project references, assembly identities and VS Code debugger paths use the new names. The existing User Secrets association and Azure resource configuration are retained.

## Limits

PDF reading order and short unformatted headings remain heuristic. Complex or uncertain tables stay text with warnings; cross-page table schemas, OCR, dehyphenation and linguistic paragraph reconstruction are not inferred. Retained margins may add retrieval noise, and large provenance metadata may need a larger token budget.

Local tests establish mechanical behavior, not live-account configuration, real-corpus retrieval quality or factual answer accuracy. Keep evaluation questions, expected values and answer keys in a separate dataset; do not use them to shape ingestion or production answers. Authentication, document authorization, rolling summaries, reranking and OCR are not implemented. The local verification commands do not reindex, deploy or provision Azure resources.

A detailed source review and cleanup record is available in [CODE_REVIEW.md](CODE_REVIEW.md).

## Push to Git manually

Create an empty remote repository named `AgenticRAGProd`, then run the following from this directory. Replace `REMOTE_URL` with that repository's clone URL.

```sh
git init -b main
git add .
git status --short
git diff --cached --stat
git commit -m "Initial commit: AgenticRAGProd"
git remote add origin "REMOTE_URL"
git push -u origin main
```
