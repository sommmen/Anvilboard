# Technical Design: Anvilboard

## 1. Document Information

| Field | Value |
|---|---|
| **Document ID** | TDD-ANV-001 |
| **Version** | 0.2 |
| **Author** | Anvilboard maintainers |
| **Reviewers** | Engineering, security, operations |
| **Date** | 2026-09-05 |
| **Status** | Draft |
| **Related PRD** | [`docs/anvilboard/prd.md`](prd.md) |
| **Related SRS** | [`docs/anvilboard/srs.md`](srs.md) |
| **Related project manifest** | [`docs/project-anvilboard.md`](../project-anvilboard.md) |

## 2. Revision History

| Version | Date | Author | Description |
|---|---|---|---|
| 0.1 | 2026-03-25 | Anvilboard maintainers | Initial technical design for the future-state product, superseding the PoC-era `SPEC.md`/`FUNCTIONAL_SPEC.md` as canonical architecture reference. |
| 0.2 | 2026-09-05 | Anvilboard maintainers | Added archive lifecycle, structured activity history, advisory dependencies, generic `ILifecycleHook<TEvent>` model, plugin persistence/events, GitHub PR artifacts, and real-time dashboard design. |

## 3. Overview

### 3.1 Background

Anvilboard exists today (`src/`) as a working proof of concept: a .NET 10 minimal-API backend with EF Core/SQLite, an Angular 22 standalone-component SPA, GitHub and Linear polling adapters, and a CLI/MCP automation surface. The PoC validated the layered architecture (`Anvilboard.Domain` → `Anvilboard.Plugins.Abstractions` → `Anvilboard.Infrastructure` → `Anvilboard.Application` → `Anvilboard.Api`/`Anvilboard.Agent`/`anvilboard-web`) and the "shared application services power every channel" principle. It has known gaps against the future-state product defined in the PRD/SRS: no workspace-scoped multi-tenant authorization, a fixed global `IssueStatus` enum instead of configurable workflows, numeric enum serialization on the API versus symbolic serialization on the agent surface, no audit trail, no idempotency support for automation mutations, and no backup/restore workflow.

### 3.2 Goals

- Evolve the existing layered .NET/Angular architecture into the future-state design without a rewrite: extend `Anvilboard.Domain`, `Anvilboard.Application`, `Anvilboard.Infrastructure`, `Anvilboard.Api`, `Anvilboard.Agent`, and `anvilboard-web` in place.
- Introduce workspace-scoped authentication/authorization as the foundational cross-cutting concern (FR-WS-001).
- Replace the fixed `IssueStatus` enum with a configurable, migration-safe workflow model (FR-WS-002, FR-WS-003) while preserving existing data via a mapping migration.
- Normalize REST and MCP/CLI contracts onto one shared, versioned, symbolic schema (FR-AUT-001) and add idempotency, correlation IDs, and a structured error taxonomy (FR-AUT-002, FR-AUT-003).
- Add an append-only audit trail (FR-OPS-001) and a verified backup/restore workflow (FR-OPS-002).
- Preserve and formalize the existing integration/plugin architecture's fault-isolation properties (FR-INT-001–003) while adding provenance and sync-health surfacing.

### 3.3 Non-Goals

- Rewriting the persistence engine away from SQLite or introducing a mandatory external database/broker (matches PRD non-goal: single-host deployment).
- Implementing provider write-back, saved views, or notifications in this design pass (PRD-ANV-011, PRD-ANV-012 are deferred; see §17).
- Building a plugin sandboxing/untrusted-code execution model; only first-party-reviewed plugin packages are in scope.
- Multi-region or horizontally scaled deployment topologies.

### 3.4 Scope

In scope: workspace/auth model, configurable workflow engine, unified board/issue read-and-write model, integration/plugin lifecycle and sync-health model, REST/CLI/MCP contract normalization with idempotency and error taxonomy, audit trail, backup/restore, and the supporting data-model migrations. Out of scope: anything listed in §3.3, and any UI visual redesign beyond what is needed to surface new workflow/provenance/audit data.

### 3.5 User Scenarios

| # | Persona | Type | Goal | Steps | Success Condition |
|---|---|---|---|---|---|
| US-001 | Workspace administrator | Human | Configure an enforceable workspace workflow. | Create teams; define ordered states and allowed transitions; attempt to create an issue using an unconfigured state. | Valid configuration is persisted; the invalid request is rejected with a specific error and creates no issue. |
| US-002 | Coordinator | Human | Triage stale work on the unified board. | Filter by provider and sync condition; inspect stale imported issues; reassign a local issue. | The filter result is consistent between UI and REST; reassignment appears in issue activity and workspace audit. |
| US-003 | Automation agent | Agent | Create an issue safely despite a network retry. | Submit a mutation with an `Idempotency-Key`; lose the response; replay the byte-equivalent request with the same authenticated actor and key. | The replay returns the original result and correlation ID; exactly one issue, activity event, and audit event exist. |
| US-004 | Integration operator | Human | Continue work while one provider degrades. | Observe GitHub polling failures; inspect integration health; continue Linear sync and perform a local issue mutation. | GitHub is marked failed or stale with diagnostic context; Linear and local paths continue without waiting for GitHub recovery. |
| US-005 | Workspace administrator | Human | Restore a workspace safely. | Start restore on a fresh host; provide a backup artifact; allow integrity and compatibility validation to complete. | Only a verified compatible backup makes the workspace usable; a corrupt or incompatible artifact fails closed and creates an audit record. |

### 3.6 Acceptance Criteria

| AC-ID | Priority | Criterion | Scenario | Verification Method |
|---|---|---|---|---|
| AC-001 | P0 | An authenticated actor can read or mutate only workspaces for which its role grants the requested action. | US-001, US-002 | Authorization integration tests cover each role/action/workspace combination. |
| AC-002 | P0 | A request for another workspace is rejected as `WORKSPACE_ACCESS_DENIED` without leaking issue, member, or workspace data. | US-001 | Negative API, CLI, and MCP contract tests assert status, code, and absence of resource fields. |
| AC-003 | P0 | A workspace workflow accepts only configured states and explicitly allowed transitions. | US-001 | Workflow service tests and transition API integration tests. |
| AC-004 | P0 | A transition from an unconfigured state or to a disallowed state returns `INVALID_WORKFLOW_TRANSITION`, naming current state, requested state, and violated rule, with no version increment. | US-001 | Boundary/error integration tests assert error payload and unchanged persisted issue. |
| AC-005 | P0 | Board queries support the canonical filters (team, workflow state, assignee, priority, project, label, provider, sync condition) with identical symbolic values across UI, REST, CLI, and MCP. | US-002 | Cross-channel contract fixtures compare normalized query results. |
| AC-006 | P0 | A page request with `limit` 1–100 returns a stable ordered page and valid opaque cursor; `limit` 0, 101, or malformed cursor returns `VALIDATION_FAILED`. | US-002 | Boundary API tests at 0, 1, 100, and 101 plus malformed-cursor tests. |
| AC-007 | P0 | A supported automation mutation is atomic and idempotent for an identical actor, key, and canonical request payload. | US-003 | Integration test replays the request and counts issues, activities, audits, and idempotency records. |
| AC-008 | P0 | Reuse of an idempotency key with a different payload or actor returns `IDEMPOTENCY_KEY_REUSED` and performs no mutation. | US-003 | Negative integration tests vary payload and actor while retaining the key. |
| AC-009 | P1 | Provider sync health records last success, failure reason, and condition without blocking another provider or local issue mutations. | US-004 | Fault-injection integration tests hold GitHub unavailable while executing Linear and local operations. |
| AC-010 | P1 | Provider-controlled fields remain read-only locally unless a workspace write-back policy is explicitly enabled. | US-004 | Negative authorization/business-rule test against an externally linked issue. |
| AC-011 | P0 | Every configuration change, issue mutation, automation mutation, integration action, and backup or restore action produces one searchable audit record with actor, workspace, action, outcome, and correlation ID. | US-002, US-003, US-005 | Integration tests query audit records following each mutation category. |
| AC-012 | P0 | Restore validates artifact integrity and compatibility before activation; corrupt, incomplete, or incompatible artifacts fail closed with a specific non-500 error and leave the target workspace unusable. | US-005 | Restore integration tests inject corrupt, missing, and incompatible artifacts. |

### 3.7 Success Metrics

| Metric | Target | Source |
|---|---|---|
| Cross-workspace authorization test pass rate | 100% of defined test cases | NFR-SEC-002 |
| Board/list p95 response time | ≤ 2s on pilot reference data/host | NFR-PERF-001 |
| Duplicate side effects on supported mutation replay | 0 | NFR-REL-001 |
| Verified backup/restore drills per release candidate | ≥ 1 | NFR-AVL-001 |
| Secret exposure findings in API/UI/logs/audit/backup | 0 | NFR-SEC-001 |

## 4. System Context

```mermaid
flowchart TB
    User["[Person]<br/>Contributor / Coordinator<br/>Creates and triages work"]
    Admin["[Person]<br/>Workspace Administrator<br/>Configures workspace, auth, integrations, recovery"]
    Agent["[Agent]<br/>Automation Agent<br/>Calls REST/CLI/MCP, may be human-supervised"]

    System["[System]<br/>Anvilboard<br/>Self-hosted workspace-scoped work coordination"]

    GitHub["[External System]<br/>GitHub<br/>Issue/PR source via REST + webhooks"]
    Linear["[External System]<br/>Linear<br/>Issue source via GraphQL polling"]
    Plugin["[External System]<br/>Approved Plugins<br/>Ingestion / webhook / post-commit extensions"]

    User -->|"Uses web UI"| System
    Admin -->|"Configures via web UI / REST"| System
    Agent -->|"Calls REST / CLI / MCP"| System
    System -->|"Polls / receives webhooks"| GitHub
    System -->|"Polls"| Linear
    System -->|"Loads and invokes"| Plugin
```

Anvilboard is the system under design. Human users and automation agents are the only consumers; GitHub, Linear, and approved plugins are external systems the integration platform depends on. There is no external identity provider in the initial release — authentication is self-hosted (see §11.1 for the open decision on identity approach).

## 5. Solution Design

### 5.1 Solution A (Recommended): Evolve the existing layered monolith in place

**Description:**

Keep the current single-deployable, layered .NET solution and Angular SPA. Add a `Workspace`/`Role`/`Principal` authorization layer at the `Anvilboard.Application` boundary (enforced by API/Agent middleware, not duplicated per-endpoint). Replace the `IssueStatus` enum with a `WorkflowState` entity referenced by stable ID, with a data migration mapping each existing enum value to an equivalent seeded state per workspace. Add `AuditEvent`, `IdempotencyRecord`, and `IntegrationHealth`/sync-condition fields to the domain and persist them via new EF Core configurations and migrations, consistent with the existing `Persistence/Configurations` pattern. Normalize REST and MCP DTOs onto one shared contract module so both channels serialize workflow state, priority, provider, and sync condition symbolically. This solution reuses the current dependency-injection wiring, plugin abstraction (`IIngestionSource`, `IWebhookReceiver`, `ILifecycleHook<TEvent>`), and per-source sync loop isolation, extending rather than replacing them.

**Architecture:**

```mermaid
flowchart TB
    subgraph "Solution A: Evolved Monolith"
        Web["anvilboard-web (Angular 22 SPA)"]
        Api["Anvilboard.Api (ASP.NET Core minimal API)"]
        Agent["Anvilboard.Agent (CLI + MCP stdio)"]
        AppSvc["Anvilboard.Application (shared use cases, authz enforcement)"]
        Domain["Anvilboard.Domain (Workspace, WorkflowState, Issue, AuditEvent, ...)"]
        Infra["Anvilboard.Infrastructure (EF Core, SQLite, migrations)"]
        Plugins["Anvilboard.Plugins.Abstractions + Integrations.GitHub/Linear"]
    end

    Web --> Api
    Agent --> AppSvc
    Api --> AppSvc
    AppSvc --> Domain
    AppSvc --> Infra
    AppSvc --> Plugins
    Infra --> SQLite[("SQLite data file")]
    Plugins --> GH["GitHub"]
    Plugins --> LI["Linear"]
```

**Pros:**
- Reuses proven layering, DI wiring, and plugin fault-isolation already validated in the PoC.
- Lowest migration risk: one deployable, one data store, incremental EF Core migrations.
- Matches the PRD's single-host/low-operational-cost constraint directly.

**Cons:**
- Workspace-scoping touches nearly every existing query/repository; large one-time refactor.
- Workflow-state migration must be airtight or existing issues become unreachable/misclassified.

### 5.2 Solution B (Alternative): Extract auth/workflow into a separate service

**Description:** Stand up a dedicated identity/workspace-configuration service (with its own datastore) that `Anvilboard.Api`/`Anvilboard.Agent` call over an internal API, decoupling authorization and workflow configuration from the core issue-tracking service.

**Pros:** Clear service boundary; independent scaling/deployment of auth logic.

**Cons:** Violates the single-host/low-operational-cost constraint (PRD non-goal); adds a network hop and a second datastore/backup surface for a self-hosted small-team product; no demonstrated need for independent scaling at pilot scale.

### 5.3 Comparison Matrix

| Criterion | Solution A (Recommended) | Solution B |
|---|---|---|
| Matches single-host deployment constraint | Yes | No |
| Migration risk | Medium (workflow-state migration) | High (new service + data split) |
| Operational burden | Low | Higher (second service/datastore) |
| Reuses validated PoC architecture | Yes | Partial |
| Time to deliver P0 scope | Faster | Slower |

### 5.4 Decision & Rationale

Solution A is selected. The PRD explicitly constrains Anvilboard to a single-host, low-operational-cost deployment; introducing a second service and datastore for authorization/workflow contradicts that constraint without a demonstrated scaling need. The existing layered architecture already isolates domain, application, infrastructure, and channel concerns, so workspace scoping and workflow configurability are additive extensions rather than a structural rewrite.

## 6. Architecture Design

```mermaid
flowchart TB
    subgraph "Client Layer"
        WebApp["[Container: Angular 22]<br/>anvilboard-web<br/>Board, issue detail, admin, dashboard, audit UI"]
    end

    subgraph "Channel Layer"
        RestApi["[Container: ASP.NET Core 10 minimal API]<br/>Anvilboard.Api<br/>Versioned REST, auth middleware, request correlation"]
        AgentHost["[Container: .NET 10 console host]<br/>Anvilboard.Agent<br/>CLI + MCP stdio JSON-RPC"]
    end

    subgraph "Application Layer"
        AppSvc["[Container: .NET 10 class library]<br/>Anvilboard.Application<br/>Use cases, authorization enforcement, idempotency, workflow engine"]
    end

    subgraph "Domain Layer"
        Domain["[Container: .NET 10 class library]<br/>Anvilboard.Domain<br/>Workspace, WorkflowState, Issue, Comment, Artifact, IssueLink, AuditEvent, ExternalLink, entities/invariants"]
    end

    subgraph "Infrastructure Layer"
        Infra["[Container: .NET 10 + EF Core 10]<br/>Anvilboard.Infrastructure<br/>Repositories, EF configurations, migrations, IArtifactStore (SQLite BLOB-backed)"]
        Sqlite[("[Container: SQLite file]<br/>Workspace data store")]
        Backup["[Container: filesystem]<br/>Backup archive store"]
    end

    subgraph "Integration Layer"
        PluginAbs["[Container: .NET 10 class library]<br/>Anvilboard.Plugins.Abstractions<br/>IIngestionSource, IWebhookReceiver, ILifecycleHook&lt;TEvent&gt;, IPluginEventPublisher, IPluginConfigStore, IPluginStateStore"]
        GH["[Container: .NET 10 class library]<br/>Anvilboard.Integrations.GitHub"]
        LI["[Container: .NET 10 class library]<br/>Anvilboard.Integrations.Linear"]
    end

    WebApp --> RestApi
    RestApi --> AppSvc
    AgentHost --> AppSvc
    AppSvc --> Domain
    AppSvc --> Infra
    Infra --> Sqlite
    Infra --> Backup
    AppSvc --> PluginAbs
    PluginAbs --> GH
    PluginAbs --> LI
    GH -->|"REST + webhooks"| GitHubExt(["GitHub"])
    LI -->|"GraphQL polling"| LinearExt(["Linear"])
```

The container structure mirrors the current `src/` project layout. `Anvilboard.Application` becomes the single authorization/idempotency/workflow enforcement point so `Anvilboard.Api` and `Anvilboard.Agent` cannot diverge in business rules — closing the existing numeric-vs-symbolic serialization gap identified in the PoC review.

## 7. Technology Stack & Conventions

### 7.1 Technology Stack Decision

| Layer | Technology | Version | Rationale |
|---|---|---|---|
| Programming language (backend) | C# | .NET 10 (`net10.0`) | Already adopted across all backend projects; no migration cost. |
| Web framework | ASP.NET Core minimal APIs | 10.0.11 (`Microsoft.AspNetCore.OpenApi`) | Already in use in `Anvilboard.Api`; low overhead, native OpenAPI support. |
| ORM / data access | Entity Framework Core (Sqlite provider) | 10.0.11 | Already in use in `Anvilboard.Infrastructure`; supports migrations needed for workflow-state/audit additions. |
| Database | SQLite | Bundled via `Microsoft.EntityFrameworkCore.Sqlite` 10.0.11 | Matches the single-host/low-operational-cost constraint; already the persisted store. |
| Frontend framework | Angular (standalone components) | 22.1.0 | Already in use in `anvilboard-web`. |
| Frontend language | TypeScript | ~6.0.2 | Already pinned in `anvilboard-web/package.json`. |
| Agent/automation host | .NET console host (CLI + MCP stdio JSON-RPC) | .NET 10 | Already in use in `Anvilboard.Agent`; extended, not replaced. |
| Testing framework | xUnit (backend), existing Angular test runner (frontend) | Target-state choice; no backend test project exists yet | No backend test project exists yet (see DEVELOPMENT.md §Testing); xUnit is the chosen framework to adopt when one is added. Frontend keeps its existing Angular test runner; no new framework introduced. |
| Containerization | Not currently containerized | N/A | Deployment packaging is an open decision (§17, OQ-005); single-host process deployment is the documented minimum. |

### 7.2 Naming Conventions

#### Code Naming

| Element | Convention | Example | Notes |
|---|---|---|---|
| C# namespaces/classes | PascalCase | `Anvilboard.Application.Workflows.WorkflowEngine` | Matches existing `Anvilboard.*` project naming. |
| C# methods/properties | PascalCase | `TransitionIssueAsync` | Matches existing `IssueService` convention. |
| Angular files/selectors | kebab-case | `issue-detail.ts`, `app-issue-detail` | Matches existing `anvilboard-web/src/app` layout. |
| Domain entity IDs | `Guid` (v7 preferred where ordering matters) | `Issue.Id` | Existing entities already use `Guid` identifiers. |

#### API Naming

| Element | Convention | Example |
|---|---|---|
| REST route segments | kebab-case, versioned prefix | `/api/v1/issues`, `/api/v1/workflow-states` |
| Query parameters | camelCase | `?workflowStateId=...&provider=GITHUB` |
| JSON fields | camelCase | `"workflowStateId"`, `"syncCondition"` |
| Enumerated values | UPPER_SNAKE_CASE symbolic strings | `"IN_PROGRESS"`, `"STALE"` |

#### Database Naming

| Element | Convention | Example |
|---|---|---|
| Tables | PascalCase (matches existing EF Core configuration output) | `Issues`, `WorkflowStates`, `AuditEvents` |
| Foreign keys | `{Entity}Id` | `WorkflowStateId`, `WorkspaceId` |
| Migrations | Timestamped EF Core migration names | `20260325_AddWorkflowStates` |

### 7.3 Parameter Validation & Input Parsing

#### Validation Rules Matrix

| Field | Rule | Error on violation |
|---|---|---|
| `workspaceId` | Must resolve to a workspace the caller is authorized for | `WORKSPACE_ACCESS_DENIED` |
| `workflowStateId` (on issue create/transition) | Must exist and be active for the issue's workspace | `REFERENCED_ENTITY_NOT_FOUND` / `INVALID_WORKFLOW_TRANSITION` |
| `title` (issue) | Required, 1–200 chars, trimmed | `VALIDATION_FAILED` |
| `idempotencyKey` (mutations) | Required for supported automation mutation endpoints; opaque string, 1–255 chars | `VALIDATION_FAILED` if missing/malformed; `IDEMPOTENCY_KEY_REUSED` if key reused with a different payload |
| `version` (optimistic concurrency) | Must match current stored version on update | `CONCURRENCY_CONFLICT` |
| Integration secret fields | Write-only; never echoed in response | Not applicable to input validation; enforced at serialization boundary |

#### Type Coercion Rules

- Symbolic enum values (workflow state, priority, provider, sync condition) are transmitted and accepted as strings, never numeric codes, across REST, CLI, and MCP.
- Timestamps are ISO-8601 UTC in all external interfaces.
- Pagination cursors are opaque, base64-encoded, server-generated tokens; clients must not construct or parse them.

#### Input Sanitization

- Free-text fields (title, description, comment body) are stored as-is and HTML-escaped only at render time in `anvilboard-web`, never mutated server-side beyond trimming and length enforcement.
- Provider webhook payloads are validated against the provider's documented signature/secret mechanism before being accepted (extends the existing `IWebhookReceiver` contract).

### 7.4 Boundary Values & Edge Cases

#### System Limits

| Limit | Value | Rationale |
|---|---|---|
| Board/list page size | Max 100 items per page (default 25) | Matches NFR-PERF-001 interactive target; consistent with template pagination defaults. |
| Idempotency key retention | Documented, finite window (exact value: open decision OQ-002) | Bounds storage growth while covering realistic client retry windows. |
| Workflow states per workspace | No hard cap; UI/validation warns above a practical threshold (e.g., 25) | Configurable workflows must not be artificially constrained, but extreme counts indicate misconfiguration. |

#### Edge Case Handling

- **Archiving a workflow state still referenced by open issues:** rejected with `VALIDATION_FAILED` naming the dependent issues/count unless the administrator supplies a replacement-state migration.
- **Duplicate provider delivery (webhook redelivery or poll overlap):** deduplicated via the `(provider, sourceKey)` unique mapping on `ExternalLink`; second delivery updates the existing record rather than creating a new one.
- **Concurrent edits to the same issue:** the losing writer receives `CONCURRENCY_CONFLICT` with the current version and is expected to refetch and retry.
- **Idempotency key reused with a different request body:** rejected with `IDEMPOTENCY_KEY_REUSED`; the original result is not returned and no new mutation is applied.

### 7.5 Business Logic Rules

#### State Machine

```mermaid
stateDiagram-v2
    [*] --> ConfiguredState: Workspace administrator defines ordered WorkflowState set
    ConfiguredState --> IssueCreated: Issue created referencing initial state
    IssueCreated --> Transitioning: Actor requests transition
    Transitioning --> IssueCreated: Transition rejected (not in allowed-transition set)
    Transitioning --> IssueCreated: Transition accepted, state updated, activity + audit recorded
    IssueCreated --> Terminal: Terminal state reached (inactive visual treatment only)
    IssueCreated --> Archived: Explicit archive operation sets ArchivedAt
    Terminal --> Archived: Explicit archive operation sets ArchivedAt
    Archived --> IssueCreated: Explicit unarchive operation clears ArchivedAt
```

The workflow engine validates every transition request against the workspace's configured allowed-transition set (an adjacency list keyed by `WorkflowState.Id`), not a hardcoded enum ordering. This directly replaces the PoC's fixed `IssueStatus` progression (`Backlog → Todo → InProgress → InReview → Done/Cancelled`), which becomes the default seeded workflow for migrated workspaces. Reaching a terminal `WorkflowState` (`IsTerminal = true`) only changes presentation (inactive visual treatment); archiving is a separate, idempotent, explicit operation that sets `Issues.ArchivedAt`, preserves comments/artifacts/links/activity/external links, and excludes the issue from default board/list/dashboard queries until `includeArchived=true` is requested.

#### Computation Rules

- Dashboard counts are computed from the same filtered query used by the board endpoint (no separate aggregation path) to guarantee reconciliation (FR-WRK-004 acceptance criterion 1).
- Sync condition (`FRESH`/`STALE`/`PAUSED`/`FAILED`) is derived from `(lastAttemptAt, lastSuccessAt, integration.isPaused, lastErrorCategory)` at read time rather than stored redundantly, avoiding drift between health state and its inputs.

#### Conditional Logic

- Provider-controlled fields on an `ExternalLink`-backed issue are read-only in the local mutation path unless a future write-back policy (PRD-ANV-011, deferred) is enabled per workspace.
- `ILifecycleHook<TEvent>` implementations execute at named `Pre*`/`Post*` lifecycle points (`PreIngest`/`PostIngest`, `PreResync`/`PostResync`, `PrePhaseChange`/`PostPhaseChange`, `PreAddComment`/`PostAddComment`, `PreAddAttachment`/`PostAddAttachment`) using a shared `HookContext<TEvent, TMetadata>(issue, trigger, metadata, ct)`. Pre-hooks may enrich/validate the pending operation through authorized application services; post-hooks run only after the core mutation is durably committed and cannot veto or roll back it. Hook failures and budget exhaustion (`HOOK_BUDGET_EXCEEDED`) are captured as diagnostics only and are captured as diagnostics, not propagated as request failures.

### 7.6 Error Handling Strategy

#### Core Principles

Every anticipated failure (validation, authorization, workflow-transition, concurrency, idempotency, rate-limit, provider) returns a specific 4xx error with a stable code and a message naming the actual cause, per SRS FR-AUT-003. `500 INTERNAL_ERROR` is reserved for unanticipated faults. Lower-layer exceptions (EF Core `DbUpdateException`, unique-constraint violations, provider HTTP client errors) are caught at the `Anvilboard.Application` boundary and translated into the §7.7 catalog before reaching any channel; they never propagate as raw stack traces to REST, CLI, or MCP callers.

#### Database Constraint → Error Translation

| Constraint violation | HTTP status | Translated error | Client-safe cause |
|---|---:|---|---|
| UNIQUE `(WorkspaceId, Key)` on `Issues` | 409 | `RESOURCE_ALREADY_EXISTS` | Names the conflicting workspace key. |
| UNIQUE `(Provider, SourceKey)` on `ExternalLinks` | Not applicable | Upsert result, not an error | Provider identity already maps to the existing link. |
| FOREIGN KEY `WorkflowStateId` on `Issues` | 404 | `REFERENCED_ENTITY_NOT_FOUND` | Names the missing or unavailable workflow state. |
| FOREIGN KEY workspace/member/integration references | 404 | `REFERENCED_ENTITY_NOT_FOUND` | Names the required reference type and identifier. |
| CHECK active-state/transition or workspace ownership guard | 409 | `INVALID_WORKFLOW_TRANSITION` / `WORKSPACE_ACCESS_DENIED` | Names the prohibited transition or scope rule. |
| NOT NULL required issue/workflow/audit field | 400 | `VALIDATION_FAILED` | Names the missing field and requirement. |
| Optimistic concurrency token mismatch | 409 | `CONCURRENCY_CONFLICT` | Supplies current version for a refetch/retry. |

#### Error Taxonomy

The stable anticipated-failure codes, retry behavior, client-safe explanations, and SRS traceability are defined in §7.7. That catalog is the single reference for REST, CLI, MCP, UI, integration, and persistence-boundary error translation.

#### Retry & Circuit Breaker Configuration

- Outbound provider adapters (`Anvilboard.Integrations.GitHub`, `.Linear`) retry transient failures with bounded exponential backoff and honor provider `Retry-After`/rate-limit headers; non-transient (4xx business) provider errors are not retried.
- Each provider's per-source sync loop is isolated (existing PoC behavior, preserved): a failing loop does not block or delay other integrations' loops or local mutation paths (NFR-REL-002).

### 7.7 Error Catalog & Traceability

Every anticipated failure has a stable catalog entry. Implementations must return the listed 4xx/5xx contract, never raw provider, database, or framework exceptions. `INTERNAL_ERROR` is deliberately absent from API contracts because it is reserved exclusively for unanticipated faults. New anticipated failures must be added here and to SRS Appendix A in the same change.

| Code | HTTP status | Trigger / source | User-facing cause and corrective action | SRS trace |
|---|---:|---|---|---|
| `AUTHENTICATION_REQUIRED` | 401 | Missing credential on any protected channel entry point. | State that authentication is required; obtain a configured credential. | FR-WS-001, FR-AUT-003 |
| `CREDENTIAL_INVALID_OR_EXPIRED` | 401 | Invalid or expired REST, CLI, or MCP credential. | State that the credential is invalid or expired; renew or replace it. | FR-AUT-003 |
| `WORKSPACE_ACCESS_DENIED` | 403 | §7.3 workspace authorization check; cross-workspace or role-denied action. | State that the actor cannot perform that action in the requested workspace; do not disclose protected data. | FR-WS-001, NFR-SEC-002 |
| `VALIDATION_FAILED` | 400 | Required/malformed `title`, key, pagination, idempotency key, request body, or restore input; NOT NULL translation. | Name the invalid or missing field, accepted range/format, and correction. | FR-WRK-002, FR-AUT-003 |
| `REFERENCED_ENTITY_NOT_FOUND` | 404 | §7.3 workflow state/reference validation; externally reachable FOREIGN KEY translation. | Name the missing reference type and identifier without exposing unauthorized resources. | FR-WS-002, FR-WRK-003 |
| `INVALID_WORKFLOW_TRANSITION` | 409 | §7.5 state-machine guard; inactive state or prohibited transition; CHECK translation. | Name current state, requested state, and violated transition rule. | FR-WS-003 |
| `RESOURCE_ALREADY_EXISTS` | 409 | Duplicate issue workspace key; externally reachable UNIQUE translation. | Name the conflicting key and workspace scope. | FR-WRK-002 |
| `CONCURRENCY_CONFLICT` | 409 | Expected version does not equal the persisted version. | Provide current version and instruct caller to refetch before retrying. | FR-WRK-004, NFR-REL-001 |
| `IDEMPOTENCY_KEY_REUSED` | 409 | Same idempotency key used with a different canonical payload or actor. | State that the key belongs to a different request; generate a new key. | FR-AUT-002, NFR-REL-001 |
| `RATE_LIMITED` | 429 | Channel request limit exceeded. | Supply `Retry-After` and state when the caller can retry. | NFR-PERF-001, NFR-MNT-001 |
| `PROVIDER_UNAVAILABLE` | 502 | Provider timeout, transport failure, or retry budget exhaustion. | Identify provider and sync operation; retry only after bounded backoff. | FR-INT-002, NFR-REL-002 |
| `INTEGRATION_PAUSED` | 409 | Operator-paused integration receives a sync action. | State that synchronization is paused and must be resumed deliberately. | FR-INT-001 |
| `BACKUP_INTEGRITY_INVALID` | 422 | Restore artifact fails checksum, manifest, schema, or compatibility validation. | Identify failed integrity/compatibility check; select a verified compatible backup. | FR-OPS-002, NFR-AVL-001 |
| `SYNC_CONFLICT` | 409 | Resync detects the linked provider record and the local issue both changed since `ExternalLink.LastSyncedVersion`. | State that a resync conflict exists on the affected field(s) and that the actor must choose keep-local/accept-remote/merge before the field updates. | FR-INT-005 |
| `ARTIFACT_STORE_UNAVAILABLE` | 502 | The configured `IArtifactStore` implementation cannot read/write content (e.g., filesystem unreachable). | State that artifact storage is temporarily unavailable; retry after the store recovers. | FR-ART-001 |
> `HOOK_BUDGET_EXCEEDED` is a lifecycle-hook execution diagnostic, not a REST contract error: the hook dispatcher records timeout/step-budget exhaustion in health, activity, and audit diagnostics, isolates the failure from the triggering operation (no partial write, no rollback of the already-committed mutation), and never surfaces it through this error table.

## 8. Detailed Design

> Per-component implementation detail (method signatures, field mappings, state machine transitions, error handling specifics) lives in the generated feature specs under `docs/features/`. See [`docs/features/overview.md`](../features/overview.md).

### 8.1 Component Overview

| Component | Responsibility | Public Interface | Dependencies | Feature Spec |
|---|---|---|---|---|
| Workspace & Authorization | Authenticates actors, resolves workspace scope, enforces role permissions for every operation | `IWorkspaceAuthorizationService`, ASP.NET Core auth middleware, Agent credential resolver | Domain (`Workspace`, `Member`, `Role`) | [`docs/features/workspace-authorization.md`](../features/workspace-authorization.md) |
| Workflow Engine | Defines/validates configurable workflow states and transitions; migrates legacy `IssueStatus` values | `IWorkflowService` | Domain (`WorkflowState`), Infrastructure | [`docs/features/workflow-engine.md`](../features/workflow-engine.md) |
| Real-time Updates | Delivers compact workspace-scoped post-commit issue/activity/dashboard-summary/eligible plugin-event envelopes without blocking mutation completion | `IRealtimeUpdatePublisher`, SignalR `WorkspaceRealtimeHub` | Workspace & Authorization, Issue & Board Service, Integration & Plugin Platform | [`docs/features/realtime-updates.md`](../features/realtime-updates.md) |
| Issue & Board Service | Issue CRUD (incl. free-form type/priority/session-state and threaded comments), kanban/list query, filtering/grouping/ordering, dashboard aggregation, archive/unarchive, structured activity history, and advisory `Blocks`/`BlockedBy` projection | `IIssueService`, `IBoardQueryService`, `ICommentService`, `RecordAndDispatchAsync` | Workflow Engine, Workspace & Authorization, Real-time Updates | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| Integration & Plugin Platform | Integration lifecycle, secret handling, ingestion/webhook execution, generic `ILifecycleHook<TEvent>` dispatch, provenance, sync-health, sync-conflict detection, outbound plugin events, and durable plugin config/state | `IIntegrationService`, `IIngestionSource`, `IWebhookReceiver`, `ILifecycleHook<TEvent>`, `IPluginEventPublisher`, `IPluginConfigStore`, `IPluginStateStore` | Issue & Board Service, Artifact Store, Real-time Updates, external providers | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| Issue Artifacts | Attaches/lists/removes file, link, and deployment artifacts on an issue behind a persistence abstraction | `IArtifactService`, `IArtifactStore` | Issue & Board Service | [`docs/features/artifacts.md`](../features/artifacts.md) |
| Issue Linking | Creates/lists/removes free-form typed relationships between two issues | `IIssueLinkService` | Issue & Board Service | [`docs/features/issue-linking.md`](../features/issue-linking.md) |
| Automation Surface (REST/CLI/MCP) | Versioned symbolic contracts, idempotency enforcement, correlation IDs, structured errors | REST controllers, CLI commands, MCP stdio handlers | All above via `Anvilboard.Application` | [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md) |
| Audit & Recovery | Append-only audit trail; backup creation and verified restore | `IAuditService`, `IBackupService` | All mutating components | [`docs/features/audit-and-recovery.md`](../features/audit-and-recovery.md) |

### 8.2 Component Interaction

```mermaid
flowchart LR
    subgraph "Anvilboard.Application"
        WA["Workspace & Authorization<br/>Authenticates + authorizes every call"]
        WE["Workflow Engine<br/>Validates transitions"]
        IB["Issue & Board Service<br/>CRUD + queries"]
        AF["Issue Artifacts<br/>Attach/list/remove"]
        LK["Issue Linking<br/>Free-form typed links"]
        IP["Integration & Plugin Platform<br/>Provenance, sync health, hooks + plugin events"]
        RT["Real-time Updates<br/>Post-commit workspace envelopes"]
        AR["Audit & Recovery<br/>Records every mutation"]
    end

    Channels["REST / CLI / MCP"] -->|"authenticated request"| WA
    WA -->|"authorized"| IB
    IB -->|"validates transition"| WE
    IB -->|"emits activity + audit"| AR
    IB -->|"attach/list"| AF
    IB -->|"link/unlink"| LK
    AF -->|"emits activity + audit"| AR
    LK -->|"emits activity + audit"| AR
    IP -->|"writes issues via"| IB
    IP -->|"dispatches lifecycle hooks through"| IB
    IP -->|"expands artifacts via post-hooks"| AF
    IP -->|"emits health + audit"| AR
    WA -->|"emits access decisions"| AR
    IB -->|"post-commit issue/activity changes"| RT
    IP -->|"eligible plugin events"| RT
```

Every mutating request flows through Workspace & Authorization first; no component accepts a request that bypasses that check. The Issue & Board Service is the single write path for issue data, used both by direct user mutations and by integration ingestion, ensuring one set of business rules governs both origins. Issue Artifacts and Issue Linking are satellite components: they never bypass the Issue & Board Service's authorization/audit path. The Integration & Plugin Platform dispatches `ILifecycleHook<TEvent>` implementations through the same `IIssueService`/`IArtifactService` surface as any other actor rather than a privileged back door. After a mutation commits, the Issue & Board Service and eligible plugin events publish compact versioned envelopes through `IRealtimeUpdatePublisher`; that delivery is asynchronous and cannot delay or fail the originating mutation.

### 8.3 Core Workflow

```mermaid
sequenceDiagram
    participant Agent as Automation Agent
    participant API as Anvilboard.Api
    participant Auth as Workspace & Authorization
    participant WF as Workflow Engine
    participant Issue as Issue & Board Service
    participant Audit as Audit & Recovery

    Agent->>API: POST /api/v1/issues/{id}/transition (Idempotency-Key, targetState)
    API->>Auth: Authenticate + authorize(workspace, actor, action)
    Auth-->>API: Authorized
    API->>Issue: RequestTransition(issueId, targetState, idempotencyKey)
    Issue->>WF: ValidateTransition(currentState, targetState, workspaceWorkflow)
    WF-->>Issue: Allowed
    Issue->>Issue: Apply transition, increment version
    Issue->>Audit: RecordActivity + RecordAudit(correlationId)
    Issue-->>API: Result(issue, correlationId)
    API-->>Agent: 200 OK { data, correlationId }
```

### 8.4 Data Flow

Ingestion follows a parallel path: a provider adapter (`Anvilboard.Integrations.GitHub`/`.Linear`) produces a normalized record, the Integration & Plugin Platform resolves it against an existing `ExternalLink` or creates one, and the record is written through the same Issue & Board Service write path used by direct user/agent mutations — guaranteeing identical validation, activity, and audit behavior regardless of origin.

## 9. API Design

### 9.1 API Overview

REST is versioned under `/api/v1/...`. CLI commands and MCP tool calls map one-to-one onto the same underlying application use cases and DTOs, differing only in transport. Full endpoint-level detail is delivered in [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md); this section defines the shared conventions. Every listed code resolves to the anticipated-failure catalog in §7.7; `500` is intentionally not a contract response.

| Endpoint | Method | Description | Auth Required | Possible Error Codes |
|---|---|---|---|---|
| `/api/v1/issues` | GET | List workspace issues using board filters and pagination. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `RATE_LIMITED` |
| `/api/v1/issues` | POST | Create a local workspace issue. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND`, `RESOURCE_ALREADY_EXISTS`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/archive` | POST | Idempotently archive an issue (sets `ArchivedAt`); excluded from default queries thereafter. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `REFERENCED_ENTITY_NOT_FOUND`, `CONCURRENCY_CONFLICT`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/unarchive` | POST | Idempotently unarchive an issue (clears `ArchivedAt`). | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `REFERENCED_ENTITY_NOT_FOUND`, `CONCURRENCY_CONFLICT`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/transition` | POST | Transition an issue state using an idempotent mutation. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND`, `INVALID_WORKFLOW_TRANSITION`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_KEY_REUSED`, `RATE_LIMITED` |
| `/api/v1/integrations/{id}/sync` | POST | Start or resume provider synchronization. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `REFERENCED_ENTITY_NOT_FOUND`, `INTEGRATION_PAUSED`, `PROVIDER_UNAVAILABLE`, `SYNC_CONFLICT`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/sync-conflicts/{conflictId}/resolve` | POST | Resolve a flagged resync conflict by choosing keep-local, accept-remote, or a field-level merge. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/artifacts` | GET, POST | List or attach a file/link/deployment artifact on an issue. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND`, `ARTIFACT_STORE_UNAVAILABLE`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/artifacts/{artifactId}` | DELETE | Remove an artifact from an issue. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `REFERENCED_ENTITY_NOT_FOUND`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/links` | GET, POST | List or create a typed relationship (e.g., `related`, `blocks`, `duplicate-of`) to another issue. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND`, `RESOURCE_ALREADY_EXISTS`, `RATE_LIMITED` |
| `/api/v1/issues/{id}/links/{linkId}` | DELETE | Remove an issue link. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `REFERENCED_ENTITY_NOT_FOUND`, `RATE_LIMITED` |
| `/api/v1/workspaces/{id}/restore` | POST | Validate and restore a workspace backup. | Yes | `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`, `VALIDATION_FAILED`, `BACKUP_INTEGRITY_INVALID`, `RATE_LIMITED` |

### 9.2 Detailed API Specifications

#### `GET /api/v1/issues`

**Description:** Returns a page of workspace issues matching the supplied board filters.

**Headers:**

| Header | Value | Required |
|---|---|---|
| Authorization | Bearer token or configured credential | Yes |
| X-Correlation-Id | Client-supplied or server-generated UUID | Recommended |

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| workspaceId | string (UUID) | Yes | — | Target workspace. |
| workflowStateId | string | No | — | Filter by workflow state. |
| assigneeId | string (UUID) | No | — | Filter by assignee. |
| provider | string | No | — | Filter by source provider (`LOCAL`, `GITHUB`, `LINEAR`). |
| syncCondition | string | No | — | Filter by sync health (`FRESH`, `STALE`, `PAUSED`, `FAILED`, `SYNC_CONFLICT`). |
| includeArchived | boolean | No | `false` | Include archived issues; default board/list/dashboard queries exclude them. |
| page | integer | No | 1 | Page number. |
| limit | integer | No | 25 | Items per page (max 100). |

**Response Body (200 OK):**

```json
{
  "data": [
    {
      "id": "b3b2...",
      "key": "ANV-142",
      "title": "Fix stale sync indicator",
      "workflowStateId": "wf-state-in-progress",
      "workflowState": "IN_PROGRESS",
      "priority": "HIGH",
      "provider": "GITHUB",
      "syncCondition": "FRESH",
      "version": 4,
      "updatedAt": "2026-03-24T10:15:00Z"
    }
  ],
  "pagination": { "page": 1, "limit": 25, "totalItems": 118, "totalPages": 5 },
  "correlationId": "b0c1..."
}
```

#### `POST /api/v1/issues/{id}/transition`

**Description:** Requests a workflow-state transition for an issue; requires an idempotency key.

**Headers:**

| Header | Value | Required |
|---|---|---|
| Authorization | Bearer token or configured credential | Yes |
| Idempotency-Key | Client-generated opaque string | Yes |

**Request Body:**

```json
{ "targetWorkflowStateId": "wf-state-done", "expectedVersion": 4 }
```

**Response Body (200 OK):**

```json
{ "data": { "id": "b3b2...", "workflowState": "DONE", "version": 5 }, "correlationId": "b0c1..." }
```

#### Error Codes

| HTTP Status | Code | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Malformed request body/query. |
| 401 | `AUTHENTICATION_REQUIRED` / `CREDENTIAL_INVALID_OR_EXPIRED` | See §7.7 catalog. |
| 403 | `WORKSPACE_ACCESS_DENIED` | Actor lacks permission for workspace/action. |
| 404 | `REFERENCED_ENTITY_NOT_FOUND` | Issue or workflow state not found. |
| 409 | `INVALID_WORKFLOW_TRANSITION` / `CONCURRENCY_CONFLICT` / `IDEMPOTENCY_KEY_REUSED` | See §7.7 catalog. |
| 429 | `RATE_LIMITED` | Caller exceeded documented rate limit. |

## 10. Database Design

### 10.1 Schema Design

#### Table: `WorkflowStates` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, NOT NULL |
| `Key` | TEXT | NOT NULL, unique per `WorkspaceId` (stable identifier, e.g. `in_progress`) |
| `DisplayName` | TEXT | NOT NULL |
| `Order` | INTEGER | NOT NULL |
| `IsTerminal` | INTEGER (bool) | NOT NULL DEFAULT 0 |
| `IsArchived` | INTEGER (bool) | NOT NULL DEFAULT 0 |

#### Table: `WorkflowTransitions` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, NOT NULL |
| `FromStateId` | TEXT (UUID) | FK → `WorkflowStates.Id`, NOT NULL |
| `ToStateId` | TEXT (UUID) | FK → `WorkflowStates.Id`, NOT NULL |

#### Table: `AuditEvents` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, NOT NULL |
| `ActorId` | TEXT | NOT NULL (member ID or agent principal identifier) |
| `Channel` | TEXT | NOT NULL (`WEB`, `REST`, `CLI`, `MCP`, `SYSTEM`) |
| `Action` | TEXT | NOT NULL |
| `TargetType` | TEXT | NOT NULL |
| `TargetId` | TEXT | NOT NULL |
| `CorrelationId` | TEXT | NOT NULL |
| `OccurredAt` | TEXT (ISO-8601) | NOT NULL |
| `ResultSummary` | TEXT | NOT NULL, redacted of secret values |

#### Table: `IdempotencyRecords` (new)

| Column | Type | Constraints |
|---|---|---|
| `Key` | TEXT | PK (composite with `WorkspaceId`, `ActorId`, `Operation`) |
| `WorkspaceId` | TEXT (UUID) | NOT NULL |
| `ActorId` | TEXT | NOT NULL |
| `Operation` | TEXT | NOT NULL |
| `RequestHash` | TEXT | NOT NULL (detects key reuse with a different payload) |
| `ResultPayload` | TEXT | NOT NULL |
| `CreatedAt` | TEXT (ISO-8601) | NOT NULL |
| `ExpiresAt` | TEXT (ISO-8601) | NOT NULL |

#### Table: `Issues` (modified)

Adds `WorkflowStateId` (replacing the `IssueStatus` enum column) and `Version` (INTEGER, optimistic concurrency token). The prior `Status` column is retained temporarily during migration and dropped in a follow-up migration once the mapping is verified (see §10.4). This change also converts `Issues.Priority` from the fixed `IssuePriority` enum column to a nullable free-form `TEXT` column (workspace-configurable, per FR-WRK-005; the current five enum values become the seeded default option set, migrated in place — no data loss), and adds:

| Column | Type | Constraints |
|---|---|---|
| `Type` | TEXT | NULL (free-form, e.g. `bug`, `feature`, `task`; FR-WRK-005) |
| `Priority` | TEXT | NULL (free-form, replaces `IssuePriority` enum; FR-WRK-005) |
| `SessionStateTitle` | TEXT | NULL (current sub-phase title, e.g. "Reviewing"; FR-WRK-006) |
| `SessionStateDescription` | TEXT | NULL (current sub-phase detail; FR-WRK-006) |
| `ArchivedAt` | TEXT (ISO-8601) | NULL (explicit archive timestamp; NULL = active; orthogonal to `WorkflowStateId`/`IsTerminal`; FR-WRK-011) |

#### Table: `Comments` (modified)

Adds `ParentCommentId` (nullable, FK → `Comments.Id`, single-level only — a reply may not itself be replied to; enforced at the application layer per FR-WRK-010 AC2, not by a recursive DB constraint).

#### Table: `ExternalLinks` (modified)

Adds `LastSyncedVersion` (INTEGER, NULL) — captures `Issues.Version` at the moment of the last successful, non-conflicted sync. Used by the sync coordinator to detect divergence: if the local issue's current `Version` no longer equals `LastSyncedVersion` *and* the incoming remote payload differs from what was last synced, a `SYNC_CONFLICT` (§7.7) is raised instead of an overwrite (FR-INT-005).

#### Table: `Artifacts` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `IssueId` | TEXT (UUID) | FK → `Issues.Id`, NOT NULL |
| `Kind` | TEXT | NOT NULL (`file`, `link`, `deployment`, `pull_request`; FR-ART-001, FR-INT-009) |
| `Title` | TEXT | NOT NULL |
| `ContentReference` | TEXT | NOT NULL (opaque locator resolved by the active `IArtifactStore`: a URL for `link`/`deployment`/`pull_request`, a store-relative key for `file`) |
| `Source` | TEXT | NOT NULL (`local`, or the originating integration, e.g. `slack-thread-expansion`, `github`) |
| `AddedById` | TEXT | NULL (member ID; NULL when added by an automation/hook) |
| `DedupKey` | TEXT | NULL (opaque provider identity, e.g. `github:{repo}#{number}`, used only by refreshable kinds for upsert-in-place; unique per `IssueId` when set) |
| `Metadata` | TEXT (JSON) | NULL (opaque key-value bag for refreshable kinds; `pull_request` stores `{ number, state, checksStatus }`) |
| `CreatedAt` | TEXT (ISO-8601) | NOT NULL |
| `UpdatedAt` | TEXT (ISO-8601) | NOT NULL (bumped on refreshable-kind upsert; FR-INT-009) |

Artifact bytes/content are never inlined in this table; `ContentReference` is resolved through `IArtifactStore` (SQLite-backed BLOB store for the first release; see [`docs/features/artifacts.md`](../features/artifacts.md)), keeping a future filesystem- or object-storage-backed implementation a swap of that one interface.

#### Table: `IssueLinks` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `SourceIssueId` | TEXT (UUID) | FK → `Issues.Id`, NOT NULL |
| `TargetIssueId` | TEXT (UUID) | FK → `Issues.Id`, NOT NULL |
| `Type` | TEXT | NOT NULL, free-form (e.g. `RELATED`, `PARENT`, `CHILD`, `DUPLICATE_OF`, `MENTIONED_IN`, `BLOCKS`) |
| `Description` | TEXT | NOT NULL, defaults to empty string (free-form detail, e.g. `"same parent"`) |
| `CreatedById` | TEXT | NULL (member ID; NULL when added by an automation/hook) |
| `CreatedAt` | TEXT (ISO-8601) | NOT NULL |

Unique constraint on `(SourceIssueId, TargetIssueId, Type)` to prevent duplicate identical links. The relationship is stored directionally (`SourceIssueId` → `TargetIssueId`) but the API/UI surfaces it from both issues (FR-LNK-001 AC1), with `BLOCKS` additionally projected onto issue detail as advisory `Blocks[]`/`BlockedBy[]` (FR-WRK-012). No cascade behavior (ownership, workflow, or notification derivation) is attached to any `Type` value, including `BLOCKS` — dependencies are never enforced (FR-LNK-001 AC3, FR-WRK-012 AC2).

#### Table: `ActivityEvents` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK |
| `IssueId` | TEXT (UUID) | FK → `Issues.Id`, NOT NULL |
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, NOT NULL (denormalized for workspace-scoped query/index) |
| `TemplateKey` | TEXT | NOT NULL (host-owned, versioned template identifier, e.g. `issue.linked`; FR-WRK-013) |
| `ActorId` | TEXT | NULL (member ID or agent principal identifier; NULL when system-generated) |
| `Parameters` | TEXT (JSON) | NOT NULL (safe display values substituted into the template) |
| `References` | TEXT (JSON) | NOT NULL (typed target list, each `{ type: Issue\|Artifact\|ExternalWorkItem\|Actor, id }`, resolved to clickable links in the UI; FR-WRK-013) |
| `CreatedAt` | TEXT (ISO-8601) | NOT NULL |

One row per `RecordAndDispatchAsync` call, persisted in the same transaction as the mutation it describes (§8.1 Issue & Board Service). Unresolvable/missing `References` entries render a safe plain-text fallback rather than failing the timeline.

#### Table: `SyncConflicts` (new)

| Column | Type | Constraints |
|---|---|---|
| `Id` | TEXT (UUID) | PK (`conflictId` in the resolve endpoint route) |
| `IssueId` | TEXT (UUID) | FK → `Issues.Id`, NOT NULL |
| `Provider` | TEXT | NOT NULL |
| `RemotePayloadSnapshot` | TEXT (JSON) | NOT NULL (the incoming remote payload preserved instead of applied) |
| `DetectedAt` | TEXT (ISO-8601) | NOT NULL |
| `ResolvedAt` | TEXT (ISO-8601) | NULL (NULL = still pending) |
| `Resolution` | TEXT | NULL (`keep-local`, `apply-remote`, or `merge`; set on resolution) |
| `ResolvedById` | TEXT | NULL (member ID of the resolving actor) |

Durable record of a detected mutable-field conflict (FR-INT-005), created by the sync coordinator and resolved via `POST /api/v1/issues/{id}/sync-conflicts/{conflictId}/resolve`. Only non-additive, mutable-field divergence reaches this table — additive comments/artifacts/links merge unconditionally beforehand and never appear here.

#### Table: `PluginConfig` (new)

| Column | Type | Constraints |
|---|---|---|
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, PK (composite with `PluginKey`, `ConfigKey`) |
| `PluginKey` | TEXT | PK (composite; the owning plugin's manifest identity) |
| `ConfigKey` | TEXT | PK (composite) |
| `Value` | TEXT | NOT NULL (routed through the same secret-provider abstraction as integration credentials when `IsSecret` is set; never returned unredacted) |
| `IsSecret` | INTEGER (bool) | NOT NULL DEFAULT 0 |
| `UpdatedAt` | TEXT (ISO-8601) | NOT NULL |

Admin-writable configuration behind `IPluginConfigStore` (FR-INT-007), namespaced per `(WorkspaceId, PluginKey)` so a plugin never owns its own schema/migration.

#### Table: `PluginState` (new)

| Column | Type | Constraints |
|---|---|---|
| `WorkspaceId` | TEXT (UUID) | FK → `Workspaces.Id`, PK (composite with `PluginKey`, `StateKey`) |
| `PluginKey` | TEXT | PK (composite) |
| `StateKey` | TEXT | PK (composite) |
| `Value` | TEXT (JSON) | NOT NULL |
| `UpdatedAt` | TEXT (ISO-8601) | NOT NULL |

Plugin-writable runtime state behind `IPluginStateStore` (FR-INT-007), e.g. a GitHub PR-to-issue correlation cache, namespaced per `(WorkspaceId, PluginKey)`.

### 10.2 ER Diagram

```mermaid
erDiagram
    WORKSPACE ||--o{ WORKFLOW_STATE : configures
    WORKSPACE ||--o{ WORKFLOW_TRANSITION : configures
    WORKFLOW_STATE ||--o{ WORKFLOW_TRANSITION : "from/to"
    WORKSPACE ||--o{ ISSUE : owns
    WORKFLOW_STATE ||--o{ ISSUE : current_state
    WORKSPACE ||--o{ AUDIT_EVENT : records
    WORKSPACE ||--o{ IDEMPOTENCY_RECORD : scopes
    WORKSPACE ||--o{ ACTIVITY_EVENT : contains
    WORKSPACE ||--o{ PLUGIN_CONFIG : scopes
    WORKSPACE ||--o{ PLUGIN_STATE : scopes
    ISSUE ||--o{ EXTERNAL_LINK : maps
    ISSUE ||--o{ COMMENT : has
    COMMENT ||--o{ COMMENT : replies_to
    ISSUE ||--o{ ARTIFACT : has
    ISSUE ||--o{ ISSUE_LINK : "source of"
    ISSUE ||--o{ ISSUE_LINK : "target of"
    ISSUE ||--o{ ACTIVITY_EVENT : records
    ISSUE ||--o{ SYNC_CONFLICT : has
    ISSUE {
        uuid Id PK
        uuid WorkspaceId FK
        uuid WorkflowStateId FK
        string Type
        string Priority
        string SessionStateTitle
        string SessionStateDescription
        datetime ArchivedAt
        integer Version
        datetime CreatedAt
        datetime UpdatedAt
    }
    WORKFLOW_STATE {
        uuid Id PK
        uuid WorkspaceId FK
        string Key
        integer Order
        boolean IsTerminal
    }
    COMMENT {
        uuid Id PK
        uuid IssueId FK
        uuid ParentCommentId FK
        string Body
        datetime CreatedAt
    }
    ARTIFACT {
        uuid Id PK
        uuid IssueId FK
        string Kind
        string ContentReference
        string Source
        string DedupKey
        string Metadata
        datetime CreatedAt
        datetime UpdatedAt
    }
    ISSUE_LINK {
        uuid Id PK
        uuid SourceIssueId FK
        uuid TargetIssueId FK
        string Type
        string Description
        datetime CreatedAt
    }
    EXTERNAL_LINK {
        uuid Id PK
        uuid IssueId FK
        integer LastSyncedVersion
    }
    ACTIVITY_EVENT {
        uuid Id PK
        uuid IssueId FK
        uuid WorkspaceId FK
        string TemplateKey
        string ActorId
        string Parameters
        string References
        datetime CreatedAt
    }
    SYNC_CONFLICT {
        uuid Id PK
        uuid IssueId FK
        string Provider
        string RemotePayloadSnapshot
        datetime DetectedAt
        datetime ResolvedAt
        string Resolution
    }
    PLUGIN_CONFIG {
        uuid WorkspaceId PK
        string PluginKey PK
        string ConfigKey PK
        string Value
        boolean IsSecret
        datetime UpdatedAt
    }
    PLUGIN_STATE {
        uuid WorkspaceId PK
        string PluginKey PK
        string StateKey PK
        string Value
        datetime UpdatedAt
    }
    AUDIT_EVENT {
        uuid Id PK
        uuid WorkspaceId FK
        string ActorId
        string Action
        string CorrelationId
        datetime OccurredAt
    }
    IDEMPOTENCY_RECORD {
        string Key PK
        uuid WorkspaceId FK
        string RequestHash
        datetime ExpiresAt
    }
```

### 10.3 Index Strategy

| Table | Index | Purpose |
|---|---|---|
| `Issues` | `(WorkspaceId, WorkflowStateId)` with a partial filter `ArchivedAt IS NULL` | Default board filter by state within workspace while excluding archived issues. |
| `Issues` | `(WorkspaceId, Key)` unique | Deduplicate/lookup by human-readable key. |
| `Issues` | `(WorkspaceId, Type)` | List/board grouping by free-form type. |
| `ExternalLinks` | `(Provider, SourceKey)` unique | Deduplicate provider ingestion. |
| `AuditEvents` | `(WorkspaceId, OccurredAt)` | Chronological audit queries per workspace. |
| `ActivityEvents` | `(IssueId, CreatedAt)` | Render an issue's chronological activity timeline. |
| `ActivityEvents` | `(WorkspaceId, CreatedAt)` | Workspace-scoped activity queries and real-time recovery. |
| `SyncConflicts` | `(IssueId, ResolvedAt, DetectedAt)` | Find pending issue conflicts and present them in detection order. |
| `PluginConfig` | `(WorkspaceId, PluginKey, ConfigKey)` unique | Namespaced configuration lookup. |
| `PluginState` | `(WorkspaceId, PluginKey, StateKey)` unique | Namespaced plugin runtime-state lookup. |
| `IdempotencyRecords` | `(WorkspaceId, ActorId, Operation, Key)` unique | Idempotent replay lookup and reuse detection. |
| `Comments` | `(IssueId, ParentCommentId)` | Thread rendering: fetch a top-level comment and its replies together. |
| `Artifacts` | `(IssueId)` | List artifacts attached to an issue. |
| `Artifacts` | `(IssueId, DedupKey)` unique when `DedupKey` is not NULL | Idempotent upsert of refreshable provider artifacts such as GitHub pull requests. |
| `IssueLinks` | `(SourceIssueId, TargetIssueId, Type)` unique | Prevent duplicate typed links; source-side lookup. |
| `IssueLinks` | `(TargetIssueId)` | Target-side lookup for bidirectional display. |

### 10.4 Migration Strategy

1. Add new tables (`WorkflowStates`, `WorkflowTransitions`, `AuditEvents`, `IdempotencyRecords`) via an additive EF Core migration; no existing table is altered in this step.
2. Seed one default workflow per existing workspace whose states/order exactly mirror the current `IssueStatus` enum (`Backlog`, `Todo`, `InProgress`, `InReview`, `Done`, `Cancelled`), preserving stable keys so historical reports remain interpretable.
3. Add `Issues.WorkflowStateId` (nullable) and `Issues.Version` in a second migration; backfill `WorkflowStateId` from the existing `Status` column using the seeded mapping; then make `WorkflowStateId` NOT NULL.
4. Ship one release with both `Status` (deprecated) and `WorkflowStateId` populated and readable, to allow rollback.
5. Drop the `Status` column in a subsequent migration only after confirming no consumer depends on it (tracked as OQ-003).
6. Add `Issues.Type`, convert `Issues.Priority` from enum to free-form `TEXT` (migrating the five existing enum values in place as literal strings), and add `Issues.SessionStateTitle`/`SessionStateDescription` plus nullable `ArchivedAt` (all additive/compatible; FR-WRK-005/006/011).
7. Add `Comments.ParentCommentId` (nullable FK → `Comments.Id`) and `ExternalLinks.LastSyncedVersion` (nullable INTEGER, backfilled from each row's current `Issues.Version` at migration time so pre-existing synced issues are treated as already-synced rather than immediately conflicted).
8. Add new `Artifacts` and `IssueLinks` tables via an additive migration; include the `pull_request` artifact kind, `DedupKey`, `Metadata`, and `UpdatedAt` from the first migration so provider upserts are idempotent. No backfill is required because both are new concepts.
9. Add `ActivityEvents`, `SyncConflicts`, `PluginConfig`, and `PluginState` as additive tables. Their initial migration has no backfill: events are recorded prospectively, conflicts are created only on future divergence, and plugin configuration/state begin empty. Create the indexes from §10.3 in the same migration.
10. Deploy the API/dashboard support for archive filtering, timeline rendering, conflict resolution, and plugin config/state only after step 9 is applied. Retain all rows on archive and sync-conflict resolution for history; neither workflow migration nor later cleanup cascades them.

## 11. Security Design

### 11.1 Authentication

- **Method:** Open decision (OQ-001). Candidates: local username/password with hashed credentials for the first release, or a pluggable credential provider abstraction to support future SSO. The PRD's small-team self-hosted framing favors starting with local credentials plus API tokens for agents.
- **Token format (agents):** Opaque workspace-scoped API tokens, hashed at rest; not JWTs unless a future SSO decision requires them.
- **Token storage (web):** HTTP-only secure session cookie for the SPA; bearer token for REST/CLI/MCP agent callers.
- **Token revocation:** Administrator can revoke a member's session or an agent's API token immediately; revocation is audited.

### 11.2 Authorization

- **Model:** RBAC, workspace-scoped.
- **Roles and Permissions:**

| Role | Permissions | Description |
|---|---|---|
| Administrator | Full workspace configuration, integration/plugin management, backup/restore, audit read | Manages the workspace. |
| Coordinator | Read/write issues, board/dashboard, integration health read | Triages and manages work. |
| Contributor | Read/write assigned or team-scoped issues, comments | Executes work. |
| Automation Agent | Scoped read/write per issued token's granted permissions; never secret read | Programmatic actor. |

- **Enforcement points:** Single enforcement point in `Anvilboard.Application` (not duplicated per-channel), invoked by REST middleware, CLI command dispatch, and MCP tool-call dispatch alike.
- **Resource-level access control:** Every query and mutation is scoped by `WorkspaceId` at the repository boundary; no query can span workspaces.

### 11.3 Data Encryption

**At Rest:** Integration secrets are stored via a secret-provider abstraction (not plaintext columns); exact algorithm/key-management choice is an open decision (OQ-004) since it depends on the deployment's ability to manage an external key store versus a local encrypted file. SQLite file-level encryption (e.g., host-disk encryption) is a documented deployment recommendation, not an application-layer guarantee, for the initial release.

**In Transit:** TLS 1.2+ is required for REST in supported production deployments; local/dev loopback exceptions are documented separately. Provider adapters use each provider's required TLS/webhook-signature scheme.

### 11.4 Audit Logging

- **Events logged:** Authentication decisions, authorization denials, configuration changes, integration lifecycle actions, issue mutations, automation mutations, backup/restore actions (matches FR-OPS-001).
- **Log format:** `AuditEvents` table fields per §10.1: workspace, actor, channel, action, target, correlation ID, timestamp, redacted result summary.
- **Retention:** Audit records are retained until workspace archival; no ordinary role can delete them (enforced at the repository/authorization layer, not just by UI omission).

## 12. Performance Design

### 12.1 Performance Targets

| Operation | Target | Source |
|---|---|---|
| Board/list query | p95 ≤ 2s | NFR-PERF-001 |
| Single-issue detail | p95 ≤ 1s | NFR-PERF-001 |
| Post-commit mutation → connected client delivery (fan-out lag) | p95 ≤ 2s under pilot-scale concurrent connections | NFR-PERF-002 |
| Committed mutation latency impact attributable to real-time publication | 0 (publication is non-blocking; never awaited by the mutating request) | NFR-PERF-002 |

### 12.2 Caching Strategy

No application-level cache is introduced in this design pass; SQLite with the index strategy in §10.3 is expected to meet pilot-scale targets. Caching is deferred until measured load data justifies it (avoids premature complexity for a single-host deployment).

### 12.3 Optimization Plan

- Dashboard aggregation reuses the indexed board query (§7.5 Computation Rules) instead of a separate materialized view, avoiding a second optimization surface to maintain.
- If pilot measurements show the 2-second target is at risk, the first optimization to evaluate is targeted read-model indexes before introducing a cache layer.
- Real-time fan-out (`IRealtimeUpdatePublisher`) dispatches compact envelopes to workspace-scoped SignalR groups off the mutation's request path (fire-and-forget with bounded, monitored queueing); a slow or disconnected client is coalesced or dropped per `realtime-updates.md` rather than allowed to add latency to other clients or to the originating mutation (NFR-PERF-002).

## 13. Observability

### 13.1 Logging Strategy

Structured logging (existing `Microsoft.Extensions.Logging.Abstractions` dependency, already present in `Anvilboard.Infrastructure`) carries correlation ID, workspace ID (where authorized to log), actor, and action on every mutating operation. MCP diagnostics are routed to stderr/configured sinks, never stdout, preserving the existing stdio JSON-RPC contract.

### 13.2 Monitoring & Metrics

- Integration sync-health (last attempt, last success, failure count) is exposed both as product data (dashboard) and as an operational signal an administrator can inspect without a separate monitoring stack, consistent with the low-operational-cost constraint.
- Request-level metrics (latency, error rate by code) are recorded per channel (REST/CLI/MCP) to validate NFR-PERF-001 and the error catalog in §7.7.
- Real-time connection metrics (active connections per workspace group, fan-out lag, coalesced/dropped-slow-client counts) are recorded to validate NFR-PERF-002 and to distinguish a healthy-but-quiet dashboard from a stalled publisher.
- `HOOK_BUDGET_EXCEEDED` occurrences are recorded as a diagnostic/audit counter per lifecycle point and plugin, never surfaced as a caller-facing REST error, so an operator can identify a runaway or slow plugin hook without affecting the originating mutation's response.

### 13.3 Alerting Rules

Formal alerting infrastructure is out of scope for the initial release given the single-host deployment target; the dashboard's freshness/sync-exception view is the documented interim substitute. Revisit if pilot operations show this insufficient.

## 14. Deployment Plan

### 14.1 Environments

Development, pilot/staging, and production, all using the same single-host deployable; environment separation is by configuration (connection string, secret provider, log level), not by architecture.

### 14.2 CI/CD Pipeline

Extend the existing build/test pipeline to run EF Core migration verification (new migrations apply cleanly against a seeded PoC-shaped database) as a required check before merge, given the workflow-state migration risk identified in §5.4/§10.4.

### 14.3 Rollback Strategy

Because §10.4 retains the deprecated `Status` column for one release, a rollback to the prior release remains possible without data loss until that column is dropped. Backup/restore (FR-OPS-002) is the rollback mechanism for any change beyond that window.

## 15. Testing Strategy Overview

- **Unit tests:** Workflow transition validation, error-taxonomy translation, idempotency key reuse detection, authorization decision logic.
- **Integration tests:** REST/CLI/MCP contract equivalence (same symbolic values, same error codes), workspace isolation (cross-workspace access denial), EF Core migration correctness against seeded legacy-shaped data.
- **End-to-end tests:** Board filter → issue detail → transition → activity/audit reconciliation; GitHub/Linear sync-health degrade-and-recover scenario.
- **Performance tests:** Board/list p95 against pilot reference dataset (NFR-PERF-001); real-time fan-out lag under pilot-scale concurrent connections, and confirmation that publication never blocks the originating mutation (NFR-PERF-002).
- **Security tests:** Cross-workspace authorization suite (NFR-SEC-002), secret-redaction verification across API/UI/logs/audit/backup (NFR-SEC-001), real-time workspace-group join authorization (a client cannot join or receive another workspace's group).
- **Archive tests:** Idempotent archive/unarchive, exclusion from default board/list/dashboard results, inclusion via `includeArchived=true`, and preservation of comments/artifacts/links/activity/external links.
- **Activity/reference tests:** Structured `ActivityEvent` reference resolution (issue/artifact/external-work-item/actor) rendering a clickable reference, and safe fallback rendering for an unresolved/missing reference.
- **Sync-merge/conflict tests:** Additive list-union merge of comments/artifacts/links producing zero collisions on resync; non-additive field conflicts flagged as `SYNC_CONFLICT`; each of `keep-local`/`apply-remote`/merge resolution paths; `SessionState` edits excluded from conflict detection.
- **Lifecycle-hook tests:** Each `Pre*`/`Post*` lifecycle point invoked with the correct `HookContext<TEvent, TMetadata>`; a `Pre*` veto/exception blocks the mutation; a `Post*` exception or budget exhaustion is caught, logged as `HOOK_BUDGET_EXCEEDED`, and never rolls back the already-committed mutation.
- **PR artifact tests:** GitHub PR artifact creation on first correlation and upsert-in-place (no duplicate) on a subsequent status refresh, keyed by `DedupKey`.

Detailed test cases are tracked separately; see [`docs/anvilboard/test-cases.md`](test-cases.md).

## 16. Milestones & Task Breakdown

| Milestone | Tasks | Estimate | Status |
|---|---|---|---|
| M1: Workspace & Auth Foundation | Workspace/role model, auth middleware, cross-workspace isolation tests | 3 weeks | Not Started |
| M2: Configurable Workflow | `WorkflowState`/`WorkflowTransition` model, migration from `IssueStatus`, transition enforcement | 2 weeks | Not Started |
| M3: Unified Board & Audit | Board/list/dashboard queries with new filters, `AuditEvents` wiring across all mutations | 2 weeks | Not Started |
| M3.5: Extended Ticket Model & List View | Free-form type/priority migration, session-state fields, threaded comments, Linear-style list view w/ grouping and ordering | 2 weeks | Not Started |
| M4: Automation Contract Normalization | Shared symbolic DTOs across REST/CLI/MCP, idempotency records, error taxonomy | 2 weeks | Not Started |
| M4.5: Artifacts & Issue Linking | `Artifacts`/`IssueLinks` schema (`Type`+`Description`, `BLOCKS` dependency projection), `IArtifactStore` abstraction (SQLite-backed), artifact/link CRUD endpoints | 2 weeks | Not Started |
| M5: Integration Provenance & Health | Sync-condition derivation, health surfacing on board/dashboard, secret redaction audit | 2 weeks | Not Started |
| M5.5: Lifecycle Hooks & Sync-Conflict Handling | `ILifecycleHook<TEvent>` contract, lifecycle points (`Pre/PostIngest`, `Pre/PostResync`, `Pre/PostPhaseChange`, `Pre/PostAddComment`, `Pre/PostAddAttachment`), execution budget diagnostics, artifact-expansion via the same hook pattern (e.g. Slack thread), `LastSyncedVersion`-based conflict detection, additive list-union merge, and dashboard-driven resolution endpoint | 2 weeks | Not Started |
| M6: Archive & Activity History | `Issues.ArchivedAt` archive/unarchive operations, `includeArchived` filtering, structured `ActivityEvents` with typed references and clickable UI rendering | 1 week | Not Started |
| M6.5: Real-Time Dashboard & Plugin Events | `IRealtimeUpdatePublisher` (SignalR), workspace-group authorization, bounded/non-blocking delivery, reconnect re-fetch behavior, `IPluginEventPublisher` | 2 weeks | Not Started |
| M6.7: GitHub PR Artifacts & Plugin Persistence | GitHub plugin PR correlation/refresh as a `pull_request` artifact, `IPluginConfigStore`/`IPluginStateStore` abstractions | 1 week | Not Started |
| M7: Backup/Restore | Backup export, restore integrity verification, audited recovery drill | 1 week | Not Started |
| M8: Hardening & Pilot Readiness | Security review, performance validation, documentation propagation | 1 week | Not Started |

## 17. Open Questions & Decision Records

| ID | Question / Decision | Status | Decision | Date |
|---|---|---|---|---|
| OQ-001 | Should unauthorized workspace access return 403 (`WORKSPACE_ACCESS_DENIED`) uniformly, or 404 to avoid confirming workspace existence in some contexts? | Open | — | — |
| OQ-002 | What is the exact idempotency-key retention window? | Open | — | — |
| OQ-003 | When is it safe to drop the deprecated `Issues.Status` column after the workflow-state migration? | Open | — | — |
| OQ-004 | What secret-at-rest storage mechanism is required for the target deployment profile (local encrypted file vs. external key management)? | Open | — | — |
| OQ-005 | Is container packaging (Docker) required for the initial release, or is a bare-process deployment sufficient? | Open | — | — |
| OQ-006 | What is the target pilot cohort size/profile used to validate NFR-PERF-001 and NFR-AVL-001? | Open | — | — |
| OQ-007 | Should `Issues.Priority` remain a fixed enum or become workspace-configurable free-form text? | Resolved | Free-form `TEXT`, seeded with the prior five-value option set per workspace, per PRD §19 OQ-7 and FR-WRK-005. | This change |
| OQ-008 | How should LLM-driven intake/triage preprocessing and artifact expansion participate in the lifecycle model? | Resolved | All lifecycle extension points, including intake preprocessing and artifact expansion, are modeled uniformly as `ILifecycleHook<TEvent>` invocations at named `Pre*`/`Post*` points (`PreIngest`/`PostIngest`, `PrePhaseChange`/`PostPhaseChange`, `PreAddAttachment`/`PostAddAttachment`, etc.), each receiving a strongly-typed `HookContext<TEvent, TMetadata>`. `Pre*` points are mutation-capable and can veto; `Post*` points are best-effort/non-vetoing and budget-bounded. Per PRD §19 OQ-8 and FR-INT-004. | This change |
| OQ-009 | What is the default behavior when a resync detects the local issue and the remote provider record both changed since the last sync? | Resolved | Flag as `SYNC_CONFLICT` and preserve both versions pending explicit actor resolution (keep-local / accept-remote / field-level merge) via a dashboard-exposed resolve endpoint; never silently overwrite. Comments, artifacts, and issue links are list-union merged additively before conflict detection so they do not themselves produce collisions; `SessionState` edits are excluded from conflict detection entirely. Per PRD §19 OQ-9 and FR-INT-005. | This change |
| OQ-010 | Should the archive state be a separate top-level model (e.g., its own table) or a lightweight marker on `Issues`? | Resolved | Lightweight nullable `Issues.ArchivedAt` timestamp, orthogonal to `WorkflowStateId`/`IsTerminal`; explicit idempotent `ArchiveAsync`/`UnarchiveAsync` operations exclude archived issues from default board/list/dashboard results (`includeArchived=false` default) while preserving all related sub-resources and history. Per FR-WRK-011. | This change |
| OQ-011 | Should `Blocks`/`BlockedBy` dependencies be a distinct persisted relationship separate from `IssueLinks`? | Resolved | No separate table: `BLOCKS` is a recognized `IssueLinks.Type` value; the dependency projection (`Blocks[]`/`BlockedBy[]`) is derived at read time from directional `BLOCKS`-typed links, and remains advisory-only with zero workflow enforcement. Per FR-WRK-012 and FR-LNK-001. | This change |
| OQ-012 | What transport delivers real-time dashboard updates, and how are workspace subscriptions authorized? | Resolved | SignalR is the default web transport behind a transport-neutral `IRealtimeUpdatePublisher` contract; a connection is placed into a server-derived `workspace:{workspaceId}` group at connect time using the caller's already-authorized workspace memberships — a client cannot request an arbitrary workspace group. Per PRD §19 (real-time) and FR-WRK-013/FR-WRK-014. | This change |

These mirror the PRD §19 open questions and must be resolved before the corresponding milestone in §16 begins, not discovered mid-implementation.

## 18. Traceability

| SRS requirement | Design section | Feature spec |
|---|---|---|
| FR-WS-001, NFR-SEC-002 | §6, §8.1 Workspace & Authorization, §11.2 | [`docs/features/workspace-authorization.md`](../features/workspace-authorization.md) |
| FR-WS-002, FR-WS-003 | §7.5 State Machine, §10.4 Migration Strategy | [`docs/features/workflow-engine.md`](../features/workflow-engine.md) |
| FR-WRK-001, FR-WRK-002, FR-WRK-003, FR-WRK-004 | §8.1 Issue & Board Service, §9, §12 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-WS-004 | §10.1 `WorkflowStates.IsTerminal` | [`docs/features/workflow-engine.md`](../features/workflow-engine.md) |
| FR-WRK-005, FR-WRK-006, FR-WRK-009 | §10.1 `Issues` (modified), §10.4 Migration Strategy step 6 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-WRK-007, FR-WRK-008 | §9.1 API Design, §10.3 Index Strategy | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-WRK-010 | §10.1 `Comments` (modified), §10.4 Migration Strategy step 7 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-ART-001, FR-ART-002 | §8.1 Issue Artifacts, §10.1 `Artifacts`, §9.1 API Design | [`docs/features/artifacts.md`](../features/artifacts.md) |
| FR-LNK-001 | §8.1 Issue Linking, §10.1 `IssueLinks` (`Type`/`Description`), §9.1 API Design | [`docs/features/issue-linking.md`](../features/issue-linking.md) |
| FR-INT-004 | §8.1, §8.2 Component Interaction, §7.7 `HOOK_BUDGET_EXCEEDED` | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| FR-INT-005 | §10.1 `ExternalLinks` (modified), §7.7 `SYNC_CONFLICT`, §9.1 sync-conflict endpoint | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| FR-INT-001, FR-INT-002, FR-INT-003, NFR-REL-002 | §8.1 Integration & Plugin Platform, §7.6 Retry & Circuit Breaker | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| FR-WRK-011 | §8.1 Archive & Housekeeping, §10.1 `Issues.ArchivedAt`, §9.1 archive/unarchive endpoints, §16 M6 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-WRK-012 | §8.1 Issue Linking, §10.1 `IssueLinks.Type` (`BLOCKS`), §9.1 API Design | [`docs/features/issue-linking.md`](../features/issue-linking.md) |
| FR-WRK-013 | §8.1 Activity History, §10.1 `ActivityEvents`, §9.1 activity endpoint, §16 M6 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| FR-WRK-014, NFR-PERF-002 | §8.1 Real-time Updates, §8.2 Component Interaction, §12 NFR-PERF-002, §13 real-time connection metrics, §16 M6.5 | [`docs/features/realtime-updates.md`](../features/realtime-updates.md) |
| FR-INT-006 | §8.1 Plugin Event Publishing, §9.1 API Design, §16 M6.5 | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| FR-INT-007 | §8.1 GitHub PR Artifacts, §10.1 `Artifacts` (`pull_request`, `DedupKey`, `Metadata`), §9.1 API Design, §16 M6.7 | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| FR-AUT-001, FR-AUT-002, FR-AUT-003 | §7.3, §7.6, §9, §10.1 `IdempotencyRecords` | [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md) |
| FR-OPS-001, FR-OPS-002, NFR-AVL-001 | §10.1 `AuditEvents`, §11.4, §14.3 | [`docs/features/audit-and-recovery.md`](../features/audit-and-recovery.md) |
| NFR-PERF-001 | §12 | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |
| NFR-SEC-001 | §11.3, §11.4 | [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md) |
| NFR-MNT-001 | §9.1, §7.7 Error Catalog | [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md) |
| NFR-PRT-001 | §14.1 | Not applicable to a single feature; covered at design level. |
| NFR-USB-001 | §3.6, dashboard/board UX described in Issue & Board Service | [`docs/features/issue-board-service.md`](../features/issue-board-service.md) |

Deferred PRD requirements (PRD-ANV-011 provider write-back, PRD-ANV-012 saved views/notifications) are intentionally not covered by this design; they require a future design revision once P0/P1 scope is delivered and validated.
