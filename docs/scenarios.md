# Scenario Walkthroughs

All scenarios run from **Change orchestration** in the workbench or through `POST /api/orchestration/runs`. Each run exposes its current plan, stage outputs, attempts, gates, decisions, and audit events.

## Greenfield

**Requirement:** Build a URL shortener with expiring links, analytics, and safe redirects.

**Decomposition:** normalize the requirement; design API, persistence, and safety boundaries; implement the API and operator experience in parallel; synchronize for validation/security review and documentation; hold release readiness for human approval.

**Validation:** the API/UI branch outputs must both succeed before either validation or documentation starts. A reviewer can inspect all outputs and must approve or reject release readiness. The run does not deploy.

To demonstrate a retry and fallback, enable **Provider timeout, then retry** and **Documentation failure, then fallback** under execution fault simulation before starting the run.

## Brownfield

**Requirement:** Improve the existing shortener's redirect reliability without breaking current API behavior.

**Decomposition:** record the compatibility constraint; identify redirect, persistence, and analytics blast radius; implement the targeted change and regression coverage as dependent workstreams; synchronize test/security and documentation gates; require approval before release readiness.

**Validation:** enable **Validation failure for replan demo** to stop at the validation gate. Open **Replan this run**, narrow the requirement and state a rationale. The superseded plan is rolled back, plan version increments, all affected outputs are invalidated, and the new plan must pass validation before human approval.

This scenario is a controlled exercise against the prototype's own link-service architecture, not an integration with an external repository.

## Ambiguous Requirement

**Requirement:** Make redirects faster while preserving the right behavior.

**Decomposition:** the requirements node identifies unresolved latency target, traffic profile, and cache policy. It enters `AwaitingClarification`; architecture and all downstream nodes remain pending.

**Human decision:** provide a measurable answer, for example: “p95 below 150 ms at 500 requests per second; cache only immutable target mappings.” The clarified requirement and actor are added to decision lineage; execution then continues through the normal dependency graph and stops for release approval.

**Validation:** verify the run is parked before clarification, resumes only after a non-empty answer, then reaches the separate approval gate. A safe-stop can cancel an active provider stage or block a waiting run.

## API Examples

Start a brownfield run:

```sh
curl -X POST http://localhost:5078/api/orchestration/runs \
  -H 'Content-Type: application/json' \
  -d '{"scenario":"brownfield","injectValidationFailure":true}'
```

Resolve a clarification:

```sh
curl -X POST http://localhost:5078/api/orchestration/runs/RUN_ID/clarification \
  -H 'Content-Type: application/json' \
  -d '{"answer":"p95 below 150 ms at 500 requests per second; cache immutable target mappings","actor":"product-owner"}'
```

Approve release readiness:

```sh
curl -X POST http://localhost:5078/api/orchestration/runs/RUN_ID/approval \
  -H 'Content-Type: application/json' \
  -d '{"approve":true,"decision":"Validation and release notes reviewed","actor":"reviewer"}'
```