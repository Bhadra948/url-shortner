# Shortline

Shortline is a runnable URL shortener and a controlled SDLC orchestration prototype. It combines a real short-link API with a deterministic, inspectable workflow engine that demonstrates requirement intake, DAG planning, parallel work, validation gates, human decisions, audit lineage, replanning, and safe-stop behavior.

## Run Locally

Prerequisites: .NET 10 SDK.

```sh
dotnet restore ./Shortener.Orchestrator.slnx
dotnet test ./Shortener.Orchestrator.slnx
dotnet run --project ./src/Shortener.Api --urls http://localhost:5078
```

Open `http://localhost:5078`. VS Code also has a **Run Shortline API** task. The API creates `src/Shortener.Api/data/shortener.db` and the append-only `audit.jsonl` at runtime. Local data is ignored by Git.

## Short-Link API

Create a link:

```sh
curl -X POST http://localhost:5078/api/links \
  -H 'Content-Type: application/json' \
  -d '{"targetUrl":"https://example.com/a-long-path","customAlias":"launch-notes"}'
```

The destination must be an absolute HTTP or HTTPS URL. Aliases are optional and limited to 4-32 letters, digits, underscores, or hyphens. Expiry is optional and must be in the future.

- `GET /api/links` lists the 100 most recently created links and click totals.
- `GET /api/links/{code}` returns link metadata.
- `GET /api/links/{code}/analytics` returns total clicks, clicks in the last 24 hours, daily counts, and top referrer hosts.
- `GET /{code}` records a click and returns a temporary redirect. Expired or inactive links return 404.
- `GET /health` reports service and static-asset readiness.

Creation is limited to 20 requests per minute per client IP; redirects are limited to 120 per minute. Limits are process-local.

## Orchestration API

- `GET /api/orchestration/scenarios` returns greenfield, brownfield, and ambiguous scenario templates.
- `POST /api/orchestration/runs` starts a run. Body fields: `scenario`, optional `requirement`, and optional fault-injection booleans `injectTransientFailure`, `injectDocumentationFailure`, `injectValidationFailure`.
- `GET /api/orchestration/runs` and `GET /api/orchestration/runs/{runId}` inspect run state.
- `POST /api/orchestration/runs/{runId}/clarification` resolves a clarification gate.
- `POST /api/orchestration/runs/{runId}/approval` records a human approve/reject decision.
- `POST /api/orchestration/runs/{runId}/replan` replaces the requirement and invalidates the superseded plan.
- `POST /api/orchestration/runs/{runId}/safe-stop` cancels in-flight provider work and blocks unfinished stages.
- `GET /api/orchestration/runs/{runId}/audit` returns run events and hash-chain verification.
- `GET /api/orchestration/metrics` returns success rate, retry/rollback counts and frequencies, MTTR, and end-to-end latency.

The workbench exposes these operations without needing to call the API manually. Scenario walkthroughs and request examples are in [docs/scenarios.md](docs/scenarios.md); components, graph, gates, and reliability semantics are in [docs/architecture.md](docs/architecture.md).

## Debug the Controller

1. In **Run and Debug**, select **.NET Launch Shortline API** and press F5. This builds the API and launches it under the C# debugger on `http://127.0.0.1:5078`.
2. Set a breakpoint inside the controller action, then send a request to that same port. For example, `GET http://127.0.0.1:5078/api/orchestration/scenarios` hits `Scenarios`; `POST http://127.0.0.1:5078/api/orchestration/runs` hits `Start`.
3. Do not use **Run Shortline API** when you expect breakpoints to hit. That task starts a normal process without the debugger. The optional **.NET Attach to Shortline API (manual process selection)** profile attaches to an already-running API; select the process serving port 5078.

## Validation

`dotnet test ./Shortener.Orchestrator.slnx` runs the xUnit suite against SQLite in-memory databases and isolated audit files. Tests cover URL policy and analytics, parallel stage dependencies, clarification and approval gates, retries and fallback, provider exceptions, safe-stop cancellation, replanning, metrics, and secret rejection. Browser smoke checks additionally exercise link creation, redirects, and the operator workbench at desktop and mobile widths.

## Safety and Limitations

- The built-in `IAgentProvider` is deterministic and credential-free. It generates reviewable stage outputs but does not call an LLM, edit source files, run generated code, or deploy. Replace it only behind the provider interface; keep policy and approval gates in the orchestration layer.
- Human approval is represented by an API decision, not authenticated identity. Do not expose this prototype to untrusted networks.
- Run snapshots and metrics are in memory and reset on process restart. The audit JSONL persists, but is not used to reconstruct workflow state.
- The SHA-256 audit chain detects accidental or unsophisticated edits; it is not a tamper-proof external ledger and is not signed with a protected key.
- SQLite uses `EnsureCreated` for a local prototype, not migrations or multi-instance production deployment. Analytics retention, privacy controls, authentication, authorization, trusted-proxy configuration, distributed rate limiting, and operational alerting remain production work.
- Validation stages are deterministic demonstrations. They do not execute a new scenario's proposed tests; the repository's actual test suite is run separately by the developer.