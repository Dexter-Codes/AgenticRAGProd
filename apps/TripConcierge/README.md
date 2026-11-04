# TripConcierge

A separate .NET 10 solution inside the existing repository. The scaffold contains one startup endpoint, `GET /`, which returns `{"service":"TripConcierge.Api","status":"ready"}`. No agent or Azure clients are configured.

| Project | References |
| --- | --- |
| `src/TripConcierge.Api` | Core |
| `src/TripConcierge.Core` | None; no package dependencies |
| `tests/TripConcierge.Tests` | Core and Api |

Core contains no domain classes yet. Tests uses xUnit and contains no sample tests. Agents, tools, workflows, MCP and travel integrations are deferred.

## Packages

Api pins `Microsoft.Agents.AI.OpenAI` to exactly `1.27.0` and `Azure.AI.OpenAI` to exactly `2.1.0`. Restore resolves their shared OpenAI dependency to `2.14.0` and System.ClientModel to `1.16.0`. Restore reported no dependency conflicts or warnings. This establishes restore and scaffold build compatibility; no SDK integration or live service behavior has been tested.

A fresh User Secrets ID is initialized for Api. No credentials have been copied or configured. Add new values separately when implementing integrations.

## Build and run

From the repository root:

```sh
dotnet restore apps/TripConcierge/TripConcierge.slnx
dotnet build apps/TripConcierge/TripConcierge.slnx --no-restore
dotnet run --project apps/TripConcierge/src/TripConcierge.Api/TripConcierge.Api.csproj --launch-profile http
```

The `http` development launch profile listens on `http://localhost:5090`. The existing RAG solution and its package references are unchanged.

Verified locally: all three projects build with zero warnings and errors, and `GET http://localhost:5090/` returns HTTP 200 with the startup JSON above. The temporary API process was stopped after this check. No model or Azure calls were made. No tests are implemented yet.

## Existing project context

Source inspection confirms the main `CHAT.md` features: PDF upload and indexing routes; Development-only corpus chat with query preparation, embeddings and Azure AI Search; Semantic Kernel answer generation; and SQLite storage of completed exchanges with bounded history. Source passages remain in immediate responses rather than stored messages. This inspection made no model or Azure calls.

The RAG API launch profiles currently use HTTP port `5018`; its documented explicit launch command uses `5080`. TripConcierge uses `5090` without modifying either RAG setting.
