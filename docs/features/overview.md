# Feature Specs Overview

> Index of implementation-facing feature specs for Anvilboard's target architecture.
> Source: `docs/anvilboard/tech-design.md` §8.1 Component Overview.
> Created: 2026-09-05

Each spec in this directory documents one architectural component from
[`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) at implementation-planning depth:
method signatures, field mappings, state-machine transitions, and error-handling specifics that
are intentionally out of scope for the technical design itself. Requirement traceability
(`FR-*`/`NFR-*`) originates in [`docs/anvilboard/srs.md`](../anvilboard/srs.md); acceptance
criteria (`AC-*`) originate in `tech-design.md` §3.6 and are extended per-component where a
feature needs more granular coverage.

## Components

| # | Feature Spec | Priority | Depends On | SRS Refs |
|---|---|---|---|---|
| 1 | [`workspace-authorization.md`](./workspace-authorization.md) | P0 | — | `FR-WS-001`, `NFR-SEC-002` |
| 2 | [`workflow-engine.md`](./workflow-engine.md) | P0 | — | `FR-WS-002`, `FR-WS-003` |
| 3 | [`issue-board-service.md`](./issue-board-service.md) | P0 | Workspace Authorization, Workflow Engine | `FR-WRK-001`–`FR-WRK-004`, `NFR-PERF-001`, `NFR-USB-001` |
| 4 | [`integration-and-plugin-platform.md`](./integration-and-plugin-platform.md) | P0 | Issue & Board Service, Workspace Authorization | `FR-INT-001`–`FR-INT-003`, `NFR-REL-002`, `NFR-SEC-001` |
| 5 | [`agent-and-automation-surface.md`](./agent-and-automation-surface.md) | P0 | Workspace Authorization, Workflow Engine, Issue & Board Service, Integration & Plugin Platform | `FR-AUT-001`–`FR-AUT-003`, `NFR-MNT-001` |
| 6 | [`audit-and-recovery.md`](./audit-and-recovery.md) | P0 | All other components | `FR-OPS-001`, `FR-OPS-002`, `NFR-AVL-001`, `NFR-REL-001` |

## Execution order and rationale

The dependency order above is also the recommended build order:

1. **Workspace Authorization** first — every other component's endpoints, CLI operations, and MCP
   tools assume an already-authenticated, workspace-scoped actor. Building anything else first
   would mean retrofitting authorization checks later, which is exactly the "bolted-on later"
   failure mode the technical design's `OQ-001`/§3.2 rationale calls out.
2. **Workflow Engine** next — the Issue & Board Service's status transitions, and the automation
   surface's transition operations, both depend on `WorkflowState`/`WorkflowTransition` existing
   and on the legacy `IssueStatus` migration path being defined.
3. **Issue & Board Service** — the core CRUD/query/dashboard surface that both the web UI and the
   automation surface consume; depends on the first two being in place.
4. **Agent & Automation Surface** and **Integration & Plugin Platform** can proceed in parallel
   once the Issue & Board Service exists — the automation surface wraps existing application
   services with idempotency/correlation/error-contract concerns, while the integration platform
   is additive (new providers, ingestion, webhooks) and does not block the core board experience.
5. **Audit & Recovery** last in terms of full completion, but its `IAuditService` interface should
   be stubbed early (Workspace Authorization already emits authorization-decision events to it) so
   that later components do not need retrofitting to emit audit events.

## Reconciling with the project decomposition

[`docs/project-anvilboard.md`](../project-anvilboard.md)'s `FEATURE_MANIFEST` groups delivery
work into three coarser streams for planning purposes: `workspace-and-board`,
`integration-and-plugin-platform`, and `agent-and-automation-surface`. The six feature specs above
are the finer-grained implementation breakdown within that manifest:

| Project manifest stream | Feature specs it covers |
|---|---|
| `workspace-and-board` | `workspace-authorization.md`, `workflow-engine.md`, `issue-board-service.md`, `audit-and-recovery.md` |
| `integration-and-plugin-platform` | `integration-and-plugin-platform.md` |
| `agent-and-automation-surface` | `agent-and-automation-surface.md` |

Audit & Recovery is a cross-cutting concern touched by every mutating component, so it is nested
under `workspace-and-board` in the manifest (the stream that owns the core domain and persistence
layer) while remaining its own feature spec for implementation planning.

## Status

All six feature specs are in **draft** status: authored against the current
[`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) and
[`docs/anvilboard/srs.md`](../anvilboard/srs.md), grounded in the existing `src/` codebase where
components already exist (e.g. `IssueService`, `DashboardService`, `SyncCoordinator`,
`BoardAgentService`) and clearly marking planned additions (e.g. `IAuthorizationService`,
`WorkflowState`, `IIntegrationService`) that do not yet exist in code. None have been implemented
or reviewed against running code yet — treat them as the target contract, not the current
behavior, until an implementation PR lands and this line is updated.

## See also

- [`docs/anvilboard/prd.md`](../anvilboard/prd.md) — product rationale and priorities behind these
  components.
- [`docs/anvilboard/srs.md`](../anvilboard/srs.md) — formal requirements and traceability matrix.
- [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) — architecture, cross-component
  interaction diagram (§8.2), API conventions, and the canonical error catalog (§7.7).
- [`docs/anvilboard/test-cases.md`](../anvilboard/test-cases.md) — test strategy and coverage
  matrix spanning all six components.
