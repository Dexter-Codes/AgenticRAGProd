# TripConcierge local workspace

The existing .NET applications serve the dashboard at **http://localhost:5090/ui/**. The original `GET /` response remains `{"service":"TripConcierge.Api","status":"ready"}`. Static UI and the planning, run-read and bridge endpoints are enabled only in Development. Bind both applications to localhost.

From the repository root, in separate terminals:

```sh
dotnet run --project src/AgenticRAGProd.Api --no-launch-profile -- --urls http://localhost:5080 --environment Development
```

```sh
dotnet run --project apps/TripConcierge/src/TripConcierge.Api --launch-profile http
```

Configure credentials using the applications' existing backend configuration / user secrets. TripConcierge reads `AzureOpenAI:Endpoint`, `ApiKey`, `ChatDeployment` and optional `RagApi:BaseUrl` (default `http://localhost:5080/`). No credentials go into browser code. RAG still requires configured Azure OpenAI, Blob Storage and AI Search. Local chat additionally requires `Chat:LocalSingleUserMode=true` in Development; the UI reads its actual availability from RAG.

## Dashboard

- **Plan a trip:** server validation, browser-generated run ID, destination selection, persisted checkpoint progress, fictional budget lines and supervisor revisions, weather and daily outlines.
- **Saved runs:** reopen by ID or recent browser-local IDs. Refresh and reopening perform only GETs. Only explicit actions PUT a planning request. Original-request edits require a new run. A 409 offers refresh and explicit replay/resume; model operations never retry automatically.
- **Knowledge & chat:** PDF upload, explicit indexing, conversation-ID reuse, source drawers and provenance. Failed chat submissions can already have persisted an exchange; inspect SQLite before resubmitting uncertain requests.
- **Retrieval & MCP:** HTTP search, SDK tool calls, source passages, tool discovery with server-provided names/descriptions/schemas.
- **API explorer:** actual implemented operations with editable JSON, request activity, status, browser duration, trace IDs and response JSON. Upload uses the multipart form in Knowledge.

Trip requests, selected IDs and recent run IDs are retained in localStorage on this origin. SQLite in `TripConcierge.Api/App_Data/trip-runs.db` remains authoritative. Read responses expose the saved request, selection, draft, version and actual checkpoint fields. Reads do not claim leases or advance workflows. There is no server-wide run listing endpoint. Chat conversation ID persists locally, but chat transcript and immediate source passages do not survive reload here; the RAG API has no history endpoint.

## Route inventory

All paths below are served on TripConcierge's port 5090. Except readiness and validation, these local workspace routes are Development-only.

| Method | Path | Behavior / model usage |
| --- | --- | --- |
| GET | `/` | Existing service readiness JSON |
| POST | `/api/trips/validate` | Existing server rules; no model |
| POST | `/api/trips/draft` | Existing one-off unsaved workflow; models / retrieval after selection |
| PUT | `/api/trip-runs/{runId}` | Existing persisted workflow and replay; preserves leases/version checks |
| GET | `/api/trip-runs/{runId}` | **New** saved request/selection/draft/version/checkpoint; unknown ID 404 |
| GET | `/api/demo/budget` | Existing fictional deterministic fixture; no model |
| POST | `/api/documents` | **New bridge** to existing RAG multipart upload |
| POST | `/api/documents/{documentId}/index?documentName=...` | **New bridge** to RAG indexing; embeddings |
| POST | `/api/chat` | **New bridge** to RAG chat; models + embeddings; 404 upstream when disabled |
| POST | `/api/travel-knowledge/search` | **New bridge** to RAG HTTP retrieval; embeddings |
| GET | `/api/mcp/tools` | **New** C# MCP SDK tool discovery; no model |
| POST | `/api/mcp/search` | **New** fixed `search_travel_knowledge` MCP call; embeddings |
| GET | `/api/local/capabilities` | **New bridge** to new Development RAG capability endpoint (`chatEnabled`) |

The bridge uses the existing TravelKnowledgeClient and configured RAG base URL. There is no generic proxy, user-selected upstream URL or arbitrary MCP tool invocation. HTTP status and response content pass through, multipart boundaries are retained, request cancellation propagates, and provider failures/timeouts produce appropriate errors. Each HTTP operation is sent once. The shared HTTP timeout is 240 seconds to support indexing/chat; workflow MCP search retains its own 25-second bound. MCP inspection has a 25-second bound.

The RAG application also exposes its existing `/mcp` streamable HTTP endpoint, restricted to localhost/127.0.0.1 hosts. MCP service registration now runs inside the shared `CreateApp` startup factory so direct test hosts receive the same registration as the executable.

## Evidence and limits

The itinerary planner currently returns generic activity IDs, **not named attractions or per-activity citations**. The UI does not invent these: it renders the activity catalog and makes returned guide passages separately inspectable. Activity costs are excluded. Transport, lodging and food are fictional demo fixtures, not quotes, availability or reservations. The itinerary supports 1–7 days even though general validation accepts longer trips. Same-day and past dates pass server validation. Weather comes from Open-Meteo's available forecast window; missing/out-of-window data is explicitly unknown.

After uploading and indexing a guide, set `TravelKnowledge:Documents:{destinationId}` to its document GUID in RAG backend configuration and restart RAG. The dashboard displays this manual step; no document-configuration mutation endpoint exists. Retrieval depends on the configured corpus and does not establish semantic correctness.

OpenTelemetry traces/metrics export to the .NET consoles in Development. There is no telemetry read API, so the UI shows availability rather than synthetic charts. Browser request timings are real measurements for this tab. Backend responses include `X-Trace-Id` on local API responses.

Inter is loaded from Google Fonts with a system-font fallback. All provider/model/document text uses text DOM nodes; no model-produced HTML is interpreted.

## Verification

```sh
dotnet build apps/TripConcierge/TripConcierge.slnx --no-restore
dotnet build AgenticRAGProd.slnx --no-restore
dotnet test apps/TripConcierge/TripConcierge.slnx --no-restore
dotnet test AgenticRAGProd.slnx --no-restore
```

`DevelopmentUiEndpointTests` uses temporary real SQLite stores and fake upstream HTTP. It checks read-only checkpoint/lease behavior, unknown IDs, multipart/error forwarding, the fixed upstream, and invalid MCP input. Browser checks in `tests/browser/check-ui.js` intercept **all `/api/**` requests** to avoid paid calls. Run through the Playwright CLI `run-code` command with the local UI running. Screenshots and assertion results are saved under `output/playwright/` (ignored local artifacts).

Live local readiness, UI hosting, unknown-run 404, capability forwarding and MCP discovery can be checked without any model calls. Actual Azure upload/indexing, model planning/chat and semantic answer quality require separately authorized live verification.

Verified for this change: both solution builds succeeded; 217 RAG tests and 4 TripConcierge integration tests passed; 23 mocked browser assertions passed at desktop and 390-pixel mobile widths. Live local MCP discovery and chat-capability forwarding succeeded. The TripConcierge build retains three existing warnings (duplicate using directives and a nullable weather argument). No paid model or embedding calls were made.
