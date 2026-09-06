# Issue & Board Service

> Feature spec for Spec-Forge implementation planning.
> Source: extracted from docs/anvilboard/tech-design.md §8.1
> Created: 2026-09-05

| Field | Value |
|-------|-------|
| Component | issue-board-service |
| Priority | P0 |
| SRS Refs | FR-WRK-001, FR-WRK-002, FR-WRK-003, FR-WRK-004, FR-WRK-005, FR-WRK-006, FR-WRK-007, FR-WRK-008, FR-WRK-009, FR-WRK-010, FR-WRK-011, FR-WRK-012, FR-WRK-013, FR-WRK-014, NFR-PERF-001, NFR-PERF-002, NFR-USB-001 |
| Tech Design Ref | §8.1 — Issue & Board Service row; also §7.5 Computation Rules, §9 API Design, §12 Performance Design |
| Depends On | workflow-engine, workspace-authorization, realtime-updates |
| Blocks | integration-and-plugin-platform, agent-and-automation-surface, audit-and-recovery, artifacts, issue-linking |

## Purpose

The Issue & Board Service is the single read/write path for issue data in Anvilboard: it creates and mutates issues, answers board/list/dashboard queries, and enforces every issue-level business rule (workflow-transition validation, optimistic concurrency, workspace scoping) exactly once so the web UI, REST API, CLI, and MCP surfaces can never observe divergent behavior. It is the write path used both by direct user/agent mutations and by the Integration & Plugin Platform's ingestion pipeline (`UpsertFromExternalAsync`), which is what guarantees identical validation, activity, and audit behavior regardless of an issue's origin (§8.4 Data Flow).

## Scope

**Included:**
- Issue CRUD: create, read, list, update assignment, add comments (optionally threaded).
- Workflow-state transition requests, delegating transition legality to the Workflow Engine (`IWorkflowService`) and applying the resulting state/version change.
- Free-form ticket taxonomy: `Type`, workspace-configurable `Priority`, and labels — all optional string fields, never validated against a hardcoded enum (FR-WRK-005).
- `SessionState` (title + description) — a lightweight, frequently-updated sub-phase indicator distinct from the issue's workflow state (FR-WRK-006).
- Board/list query with filtering and grouping by team, workflow state, assignee, priority, type, project, label, provider, and synchronization condition (FR-WRK-001, FR-WRK-005).
- List view: a Linear-style flat/grouped list rendering of the same filtered query used by the board, with grouping (by workflow state, type, priority, assignee, or label) and ordering (by created date, modified date, priority, or manual rank) (FR-WRK-007, FR-WRK-008).
- Dashboard summary aggregation (workflow distribution, source distribution, freshness/sync exceptions, assignee load) computed from the same filtered query as the board endpoint (FR-WRK-004).
- Pagination (page/limit, opaque cursor) over board/list results.
- Upserting issues produced by ingestion plugins via `NormalizedIssue`, deduplicated on `(Provider, SourceKey)`.
- Archiving/unarchiving issues through an explicit, idempotent lifecycle operation; archived issues leave normal board/list/dashboard queries by default while their history, links, comments, and artifacts remain intact (FR-WRK-011).
- Surfacing `BlockedBy` and `Blocks` dependency markers from `BLOCKS` issue links on the issue-detail read model; they inform users but never gate a workflow transition (FR-WRK-012).
- Rendering rich activity records from typed templates with structured cross-references, so the UI can display text such as "arjen linked COM-234" with `COM-234` as a navigable issue reference (FR-WRK-013).
- Resolving external synchronization conflicts from the dashboard after additive comments, artifacts, and issue links have been list-union merged (FR-INT-005).
- Publishing compact, versioned issue/activity/dashboard-change events after committed mutations for `realtime-updates`, without making the mutation wait for connected clients (FR-WRK-014, NFR-PERF-002).
- Recording `ActivityEvent`s and invoking the named `Pre*`/`Post*` `ILifecycleHook<TEvent>` pipeline points at their documented lifecycle boundaries.

**Excluded:**
- Workflow state/transition *definition* and legality rules (owned by `workflow-engine`; this component only calls `IWorkflowService`).
- Actor authentication and workspace/role permission evaluation (owned by `workspace-authorization`; this component receives an already-authorized `WorkspaceId`/actor).
- Provider polling, webhook signature verification, and plugin discovery (owned by `integration-and-plugin-platform`; this component only exposes the upsert path those components call into).
- Idempotency-key storage/replay and REST/CLI/MCP contract shaping (owned by `agent-and-automation-surface`).
- Audit-record persistence and retention (owned by `audit-and-recovery`; this component only emits the domain event that component records).
- Assignment/ownership *authority* — `AssigneeId`/`TeamId` are stored, filterable, and displayed as non-prominent metadata only; the upstream remote system (where one is linked) remains the source of truth for who works an issue, and this component never blocks a mutation on assignee/team presence (FR-WRK-009).
- Artifact and issue-link persistence (owned by `artifacts.md` and `issue-linking.md` respectively; this component only surfaces their counts/summaries on the issue detail read model).

## Core Responsibilities

1. **Issue CRUD** — create a local issue, fetch one issue, list issues by filter, change assignment, add a comment (root or threaded reply).
2. **Workflow-state transition** — validate a requested transition through the Workflow Engine, apply it, and increment the optimistic-concurrency version.
3. **Board/list query** — return a filtered, paginated, deterministically ordered set of issues scoped to one workspace, renderable as either a per-phase kanban board or a grouped/ordered flat list.
4. **Dashboard aggregation** — compute workflow/source/freshness/assignee summaries by reusing the board query's filter predicate.
5. **External upsert** — deduplicate and persist issues/comments produced by ingestion plugins, on the same write path as direct mutations.
6. **Activity, template rendering + lifecycle hooks** — record an `ActivityEvent` for every mutation, preserve its typed template/reference payload for rich UI rendering, and invoke the applicable `Pre*`/`Post*` `ILifecycleHook<TEvent>` point with its concrete metadata type.
7. **Free-form taxonomy & session state** — accept and persist optional `Type`, `Priority`, and `SessionState` (title + description) fields as opaque strings, with no cross-field validation beyond length limits.
8. **Archive lifecycle** — archive or unarchive one issue without deleting or cascading to its comments, artifacts, issue links, activity, or external links.
9. **Dependency read model** — project directional `BLOCKS` links into `BlockedBy` and `Blocks` summaries without turning either relationship into transition validation.
10. **Conflict resolution** — present pending external non-additive-field conflicts and apply the user's `keep-local`, `apply-remote`, or explicitly merged resolution through the same versioned write path.
11. **Real-time emission** — publish workspace-scoped post-commit change notifications to `realtime-updates` using coalescible payloads that let the dashboard patch affected rows without refetching or reflowing the entire board.

## Interfaces

### Inputs
- **`CreateAsync` / `ChangeStatusAsync` / `AssignAsync` / `AddCommentAsync` / `ArchiveIssueAsync` / `UnarchiveIssueAsync` calls** (REST controllers, CLI commands, MCP tool handlers, all via `Anvilboard.Application`) — authenticated, workspace-scoped mutation requests.
- **Sync-conflict resolution requests** (`keep-local`, `apply-remote`, or a client-supplied merged non-additive field set) from the dashboard endpoint; every resolution is versioned and activity-recorded.
- **Board/list query parameters** (`workspaceId`, `workflowStateId`, `assigneeId`, `provider`, `syncCondition`, `type`, `priority`, `groupBy`, `orderBy`, `includeArchived`, `page`, `limit`) — from `GET /api/v1/issues` and the equivalent CLI/MCP list operations (tech-design §9.2).
- **`NormalizedIssue` / `NormalizedComment` records** (from `integration-and-plugin-platform`'s `IIngestionSource.SyncAsync` and `IWebhookReceiver.HandleAsync` results) — provider-agnostic upsert input.
- **Transition requests** (`targetWorkflowStateId`, `expectedVersion`, `Idempotency-Key`) — from `POST /api/v1/issues/{id}/transition`.
- **`SessionState` update requests** (`title`, `description`) — from `PATCH /api/v1/issues/{id}/session-state`, callable by both human actors and enrichment/automation hooks (FR-WRK-006).
- **`AddCommentAsync(issueId, body, parentCommentId?, actorId, ct)` calls** — `parentCommentId` is optional; when present it must reference an existing root-level (non-reply) comment on the same issue (FR-WRK-010).

### Outputs
- **`Issue` / `Comment` domain entities** — returned to callers and persisted via `AnvilboardDbContext`.
- **`ActivityEvent` records** — persisted per mutation with a typed template key, display parameters, and structured entity references; consumed by `audit-and-recovery` for the audit trail and by the UI's rich activity timeline (FR-WRK-003, FR-WRK-013).
- **Issue-detail dependency summaries** — `BlockedBy[]`/`Blocks[]` projections of `BLOCKS` links, each with the related issue identifier/title and navigable target (FR-WRK-012).
- **`DashboardSummary`** — read model consumed by the dashboard endpoint/UI.
- **`RealtimeIssueChange` / `RealtimeActivityChange` payloads** — workspace-scoped, post-commit notifications passed to `realtime-updates`; payloads identify the changed resource/version and affected summary keys rather than carrying a full board snapshot (FR-WRK-014).
- **Board/list result page** — `{ data[], pagination, correlationId }` shape consumed by REST/CLI/MCP (tech-design §9.2).
- **List view result page** — `{ groups[]: { groupKey, items[] }, pagination, correlationId }` — the same underlying filtered/ordered query as the board, reshaped into named groups instead of per-workflow-state kanban columns (FR-WRK-007).

### Dependencies
- **`workflow-engine` (`IWorkflowService.ValidateTransitionAsync`)** — validates whether a requested transition is legal for the issue's workspace workflow; returns a `TransitionValidationResult` (`Allowed`/`Denied(INVALID_WORKFLOW_TRANSITION, ...)`) this component never overrides.
- **`workspace-authorization`** — supplies the authorized `WorkspaceId`/actor context this component scopes every query and mutation by; never queried span-workspace.
- **`Anvilboard.Plugins.Abstractions` (`IPluginRegistry`, `ILifecycleHook<TEvent>`)** — dispatches the concrete named `Pre*`/`Post*` lifecycle point; `Pre*` may deny before commit and `Post*` is best-effort after commit (see `integration-and-plugin-platform.md`).
- **`realtime-updates` (`IRealtimeUpdatePublisher`)** — accepts workspace-scoped, post-commit notifications for SignalR fanout; transport availability or slow clients never delay this service's transaction.
- **`issue-linking` (`IIssueLinkService`)** — supplies directional `BLOCKS` links for detail projections and preserves zero-cascade semantics.
- **`Anvilboard.Infrastructure.Persistence` (`AnvilboardDbContext`)** — EF Core/SQLite persistence.

## Data Flow

```mermaid
sequenceDiagram
    participant Caller as REST/CLI/MCP caller
    participant IBS as Issue & Board Service
    participant WF as Workflow Engine
    participant Hooks as ILifecycleHook<PhaseChangeMetadata> plugins
    participant DB as AnvilboardDbContext (SQLite)
    participant RT as Realtime Updates

    Caller->>IBS: RequestTransition(issueId, targetWorkflowStateId, expectedVersion)
    IBS->>WF: ValidateTransitionAsync(workspaceId, currentStateId, targetStateId)
    WF-->>IBS: TransitionValidationResult: Allowed() | Denied(INVALID_WORKFLOW_TRANSITION)
    IBS->>Hooks: PrePhaseChange(metadata) [sequential; may deny]
    Hooks-->>IBS: Allow() | Deny(reason)
    IBS->>DB: Update Issue.WorkflowStateId, increment Version; insert ActivityEvent
    DB-->>IBS: Persisted
    IBS-->>Caller: Result(issue, correlationId)
    IBS->>Hooks: PostPhaseChange(metadata) [bounded; best-effort]
    IBS->>RT: Publish changed issue/activity summary [post-commit; non-blocking]
```

## Key Behaviors

### `CreateAsync(WorkspaceId, TeamId, title, description?, type?, priority?, projectId?, assigneeId?, createdById?, ct)`

Current implementation (`Anvilboard.Application/Issues/IssueService.cs`) validates `title` is non-empty, loads the `Team` to mint the next `"{TeamKey}-{N}"` key, and inserts an `Issue` with `Source = IntegrationProvider.Local`. Future-state additions required by FR-WS-001/FR-WRK-002/FR-WRK-005:

1. Accept and validate `WorkspaceId` first; reject with `WORKSPACE_ACCESS_DENIED` if the caller is not authorized for it (delegated to `workspace-authorization`, enforced before this method is reached).
2. Resolve `WorkflowStateId` (not the fixed `IssueStatus` enum) for the issue's initial state; a missing state returns `REFERENCED_ENTITY_NOT_FOUND`, while an archived state returns `INVALID_WORKFLOW_TRANSITION` (tech-design §7.7 catalog split).
3. Validate `idempotencyKey` (required for the automation surface's create endpoint per FR-AUT-002) is well-formed; the automation-surface layer owns replay detection, but this method must accept and forward the resolved key so its result can be cached against it.
4. Initialize `Issue.Version = 1` (new `Version` column, tech-design §10.1).
5. `type` and `priority` are optional free-form strings (`Issue.Type`, `Issue.Priority` as `TEXT`, not enums) — accepted verbatim (trimmed, length-capped per tech-design §10.1) with no membership validation against a fixed list, so a Linear-sourced `"Bug"` and a Jira-sourced `"Defect"` are both valid and stored as given (FR-WRK-005). Omitting either leaves the column `NULL`; `NULL` is a distinct, filterable "unset" value, not coerced to a default string.
6. `Issue.CreatedAt` is set once at insert and never mutated thereafter; `Issue.UpdatedAt` is set equal to `CreatedAt` on insert and updated on every subsequent mutation — these two timestamps are exposed as independently sortable/filterable fields everywhere an issue's date is shown or queried (FR-WRK-008), never collapsed into a single "date" field.
7. Persist and call `RecordAndDispatchAsync(issue, ActivityEventType.Created, ...)`.

### `RequestTransitionAsync(IssueId, targetWorkflowStateId, expectedVersion, actorId, ct)` (planned; replaces `ChangeStatusAsync`)

Replaces the current enum-based `ChangeStatusAsync(IssueId, IssueStatus, ...)` with a `WorkflowStateId`-based transition per tech-design §7.5/§8.3:

1. Load the issue; if not found, return `REFERENCED_ENTITY_NOT_FOUND`.
2. If `issue.Version != expectedVersion`, return `CONCURRENCY_CONFLICT` with the current version.
3. Call `IWorkflowService.ValidateTransitionAsync(workspaceId, issue.WorkflowStateId, targetWorkflowStateId, ct)`; on `Denied(INVALID_WORKFLOW_TRANSITION, ...)` return that result unchanged — naming current state, requested state, and the violated rule — with no version increment (tech-design AC-004; `workflow-engine.md` AC-004).
4. Invoke registered `PrePhaseChange` hooks sequentially with `PhaseChangeMetadata(FromPhase, ToPhase)`; the first `HookResult.Deny(reason)` returns a validation-style denial to the initiating caller without persisting or incrementing the issue. A hook budget breach is the same forced denial (`HOOK_BUDGET_EXCEEDED`).
5. Apply `issue.WorkflowStateId = targetWorkflowStateId`, increment `issue.Version`, set `issue.UpdatedAt`; if the target state `IsTerminal`, set `CompletedAt` (mirrors the existing `IsTerminal()` logic in `IssueStatusExtensions`, moved to operate on `WorkflowState.IsTerminal`).
6. Persist the issue and templated `ActivityEvent(StatusChanged)` within one transaction, return the committed result, then invoke `PostPhaseChange` hooks concurrently under their lifecycle budget and publish the compact real-time change event. Post-commit work cannot alter this successful transition result.

### `ListAsync` → planned `IBoardQueryService.QueryAsync(BoardQuery query, ct)`

The current `ListAsync(TeamId?, IssueStatus?, MemberId?, ct)` filters only by team/status/assignee and sorts client-side (documented as acceptable only "at this project's target scale"). FR-WRK-001/FR-WRK-005 require filtering/grouping by team, workflow state, assignee, priority, type, project, label, provider, and sync condition, plus deterministic pagination (tech-design AC-005/AC-006):

| Filter field | Type | Behavior |
|---|---|---|
| `workspaceId` | required | Always applied first; no query may span workspaces. |
| `workflowStateId` | optional | Exact match against `Issue.WorkflowStateId`. |
| `assigneeId`, `provider`, `projectId`, `priority`, `type`, `labelId` | optional | Exact/contains match as applicable; `priority`/`type` match against the free-form `TEXT` column verbatim (case-insensitive), with no enum coercion. |
| `syncCondition` | optional | Derived filter (see below), not a stored column. |
| `page`, `limit` | optional, default `page=1`, `limit=25`, max `limit=100` | `limit` outside `1..100` or a malformed cursor returns `VALIDATION_FAILED` (tech-design AC-006). |

`syncCondition` (`FRESH`/`STALE`/`PAUSED`/`FAILED`) is not persisted on `Issue`; it is derived at read time from the linked `IntegrationHealth`/`ExternalLink` record's `(lastAttemptAt, lastSuccessAt, integration.isPaused, lastErrorCategory)`, per tech-design §7.5 Computation Rules, to avoid drift between health state and its inputs. A no-result page is a successful response that still echoes the active filters (SRS FR-WRK-001 AC 3).

### `DashboardService.GetSummaryAsync` (existing, `Anvilboard.Application/Dashboard/DashboardService.cs`)

Conceptually part of this component's dashboard-aggregation responsibility (tech-design §8.1 row). The existing implementation materializes the filtered `Issues` set client-side and computes `byStatus`, `bySource`, 7-day created/completed counts, and per-assignee open-issue load. Future-state requirement (FR-WRK-004): the aggregation query predicate must be the *same* predicate object/expression the board endpoint uses (not a parallel hand-written filter), so summary counts reconcile with the matching board query by construction rather than by convention.

### `UpsertFromExternalAsync(NormalizedIssue, ct)` (existing)

Resolves an existing `ExternalLink` by `(Provider, SourceKey)`; if none exists, invokes `PreIngest`, creates the `Issue` and `ExternalLink` together, records activity, then invokes `PostIngest`. If one exists and `SyncFingerprint` is unchanged, it no-ops. On changed input, it invokes `PreResync`, list-union merges additive remote comments, artifacts, and issue links (deduplicating provider external IDs where available), then compares only mutable `Title`, `Description`, `Priority`, `Labels`, and `WorkflowStateId` against `Issue.Version`/`ExternalLink.LastSyncedVersion`. `SessionState` is excluded. A safe mutable update persists normally; a competing local mutable update preserves both current local data and the remote snapshot as `SYNC_CONFLICT` for dashboard resolution. After a successful commit it records structured activity, invokes `PostResync`, and publishes the compact update. This is the single call site `integration-and-plugin-platform`'s `SyncCoordinator` and webhook endpoints use — it must remain the *only* path that creates or mutates a provider-sourced `Issue`, so validation and activity/audit recording cannot diverge from direct mutations (tech-design §8.4).

### `RecordAndDispatchAsync(issue, templateKey, actorId, parameters, references, postCommitPoint?, metadata, ct)` (planned)

Persists one `ActivityEvent` in the mutation's transaction. `templateKey` selects a host-owned, versioned activity template; `parameters` supplies safe display values and `references` stores typed targets (`Issue`, `Artifact`, `ExternalWorkItem`, or `Actor`) separately from rendered prose. For example, a link operation stores template `issue.linked` with actor and target issue references; the UI renders "arjen linked COM-234" by resolving `COM-234` to a clickable local/detail route, while API clients receive the same structured reference list rather than having to parse display text. Unknown/missing references render a safe plain-text fallback and never make the activity timeline fail.

After commit, this method emits the audit event `audit-and-recovery` requires (workspace, actor, channel, action, target, correlation ID, timestamp), invokes the applicable named `Post*` hook point concurrently via `Task.WhenAll`, and publishes the compact real-time activity/issue change. `Post*` exceptions or budget exhaustion are caught and logged as `HOOK_BUDGET_EXCEEDED` where applicable; they never roll back or change the already-successful mutation. Pre-commit gate points are dispatched by their owning operation, not by this post-commit helper.

### `UpdateSessionStateAsync(IssueId, title?, description?, actorId, ct)` (new, FR-WRK-006)

`SessionState` is a lightweight, high-churn "what's happening right now" indicator distinct from `WorkflowStateId` — e.g. an issue sitting in the "In Progress" workflow state can carry a `SessionState` of `("Reviewing", "Checking AST before continuing implementation")` that changes many times without ever triggering a workflow transition.

1. Both `title` and `description` are optional, independently nullable, free-form strings capped at the length limits in tech-design §10.1; omitting both is a `VALIDATION_FAILED` no-op (at least one must be supplied).
2. Updating `SessionState` does **not** increment `Issue.Version` and does **not** require `expectedVersion` — it is deliberately excluded from the optimistic-concurrency envelope so frequent automated updates (e.g. an LLM agent narrating its own progress) cannot collide with human workflow-transition edits.
3. Updating `SessionState` still sets `Issue.UpdatedAt`, records an `ActivityEvent` of type `SessionStateChanged`, and publishes the changed issue summary through real-time updates. It is excluded from synchronization conflict detection and does not invoke a phase-transition hook.
4. Both the simple issue view and the issue detail view render `SessionState.Title` + `SessionState.Description` alongside `Title`/`Description`; a `NULL` `SessionState` renders nothing (no placeholder text).

### Archive, dependency, and synchronization-conflict operations (new)

`ArchiveAsync` sets nullable `Issue.ArchivedAt`, and `UnarchiveAsync` clears it. Both require workspace access, are idempotent for the requested final state, preserve every comment, activity, artifact, issue link, dependency marker, and external link, and record a structured archive activity. They are not workflow transitions: phase and terminal-state semantics remain unchanged. Board/list/detail queries exclude archived tickets by default and include them only with `includeArchived=true`; each committed change publishes a compact real-time update.

Issue detail projects `BLOCKS` links from `issue-linking.md` as `Blocks` and the inverse as `BlockedBy`. These are advisory dependency markers only: neither create nor phase transition may be rejected, delayed, or automatically moved because a dependency is open or completed.

`ResolveSyncConflictAsync(issueId, resolution, expectedVersion, ct)` resolves a persisted `SYNC_CONFLICT` from the dashboard with exactly one explicit choice: `keep-local`, `apply-remote`, or a caller-supplied merged mutable-field result. Before conflict detection, external resync list-union merges additive comments, artifacts, and issue links, deduplicating provider external IDs where available. The resolver applies the selected `Title`, `Description`, `Priority`, `Labels`, and `WorkflowStateId` result, clears the remote snapshot/conflict marker, advances the version, records structured activity, and publishes a post-commit update. `SessionState` is neither compared nor resolved.

### List view grouping and ordering (new, FR-WRK-007/FR-WRK-008)

The list view renders the identical `BoardQuery` result set as the kanban board, reshaped into named groups instead of per-workflow-state columns:

| `groupBy` value | Grouping key | Notes |
|---|---|---|
| `workflowState` (default) | `Issue.WorkflowStateId` | Equivalent grouping to the kanban board's columns, in `WorkflowState.Order`. |
| `type` | `Issue.Type` | Issues with `Type IS NULL` are grouped under a single `"Untyped"` bucket, always rendered last. |
| `priority` | `Issue.Priority` | Issues with `Priority IS NULL` are grouped under a single `"No priority"` bucket, always rendered last. |
| `assignee` | `Issue.AssigneeId` | Unassigned issues grouped under `"Unassigned"`, always rendered last. |
| `label` | `IssueLabel` join | An issue with N labels appears in N groups; an issue with zero labels appears once under `"No label"`. |

Within each group, `orderBy` accepts `createdAt` (default: newest first), `updatedAt`, `priority` (workspace-defined priority rank, falling back to alphabetical if no rank is configured), or `manual` (a per-group `Rank` value, reordered via drag-and-drop and persisted as a fractional-index string to avoid renumbering siblings on every move). Group order itself is stable and deterministic for a given `groupBy` (e.g. `WorkflowState.Order` ascending, or the fixed bucket order shown above) so repeated identical queries never reshuffle groups.

### Threaded comments (new, FR-WRK-010)

`AddCommentAsync` accepts an optional `parentCommentId`:

1. If `parentCommentId` is supplied and does not resolve to an existing comment on the same `IssueId`, return `REFERENCED_ENTITY_NOT_FOUND`.
2. If `parentCommentId` resolves to a comment that *itself* has a non-null `ParentCommentId` (i.e., the caller is trying to reply to a reply), return `VALIDATION_FAILED` naming "comments support a single level of replies only" — threading is exactly one level deep, enforced here at the application layer rather than via a recursive database constraint.
3. A root comment (`ParentCommentId IS NULL`) may accumulate any number of direct replies; replies render nested one level under their parent, ordered by `CreatedAt` ascending, in both the simple and detail issue views.
4. Comments created by ingestion/sync (`UpsertFromExternalAsync`) are always root comments; a provider's own reply-to-comment semantics (if any) are not currently mapped to `ParentCommentId` — this is an explicitly deferred scope item (see PRD OQ-009 disposition).

### Team/owner as non-blocking metadata (new, FR-WRK-009)

`TeamId` and `AssigneeId` remain stored, filterable, and displayed fields, but neither is prominent nor required by this component's validation:

1. `CreateAsync` never requires `assigneeId`; an unassigned issue is valid and renders as such (see the `"Unassigned"` list-view bucket above).
2. No method in this component checks "is the caller a member of this issue's team" as an authorization gate — that remains `workspace-authorization`'s exclusive concern (already excluded from this component's scope), and is deliberately *not* extended to per-issue ownership.
3. When an issue is linked to a remote provider, `AssigneeId`/`TeamId` are treated as denormalized display metadata synced from the remote on each successful `UpsertFromExternalAsync`, never as a local authority that could drift from or override the upstream assignment.

## Constraints

- **Single write path**: no component other than Issue & Board Service may insert or update an `Issue` row; ingestion, webhooks, and direct mutations all funnel through `CreateAsync`/`RequestTransitionAsync`/`UpsertFromExternalAsync`.
- **Workspace scoping**: every query and mutation must be scoped by `WorkspaceId` at the repository boundary (NFR-SEC-002); this component trusts but does not itself perform authorization — it requires an already-resolved, authorized `WorkspaceId`.
- **No separate aggregation path**: dashboard counts must reuse the board query's filter, not a hand-maintained duplicate (tech-design §7.5).
- **Typed lifecycle semantics**: named `Pre*` hooks execute sequentially before their owning mutation and may deny it; `Post*` hooks execute only after durable commit, are bounded and best-effort, and cannot veto or change the completed request result. Hooks receive typed metadata and use application services for follow-on mutations (FR-INT-003).
- **Performance**: board/list query p95 ≤ 2s, single-issue detail p95 ≤ 1s under pilot reference load (NFR-PERF-001); the `(WorkspaceId, WorkflowStateId)` and `(WorkspaceId, Key)` indexes (tech-design §10.3) are required, not optional, for this target.
- **Provider-controlled fields**: fields on an `ExternalLink`-backed issue are read-only in the local mutation path unless a workspace write-back policy is explicitly enabled (deferred; PRD-ANV-011).
- **No enum for `Type`/`Priority`**: `Issue.Type` and `Issue.Priority` are free-form `TEXT` columns with no CHECK constraint or application-layer allow-list — a workspace remains free to use whatever vocabulary its upstream ticketing system uses (FR-WRK-005).
- **`SessionState` bypasses optimistic concurrency**: `UpdateSessionStateAsync` never reads or compares `Issue.Version` (see Key Behaviors above); it is the one mutation path on this entity intentionally excluded from the `expectedVersion` envelope.
- **Single level of comment threading**: a comment whose `ParentCommentId` is itself non-null may never be the target of another comment's `ParentCommentId` (FR-WRK-010).
- **Team/owner never gate a mutation**: no method in this component may reject a request solely because `AssigneeId`/`TeamId` is unset or because the actor is not the assignee (FR-WRK-009).
- **Archive is a zero-cascade visibility state**: archive/unarchive preserve all satellite data and workflow state; archived issues are absent unless explicitly requested.
- **Dependencies are advisory**: `BlockedBy`/`Blocks` projections never enforce sequencing or block a workflow transition.
- **Additive data merges before conflicts**: comments, artifacts, and issue links union merge on resync; only configured mutable ticket fields can yield `SYNC_CONFLICT`, and `SessionState` cannot yield one.
- **Incremental real-time delivery**: only compact, workspace-scoped post-commit deltas are published; consumers patch affected summaries/rows rather than refresh the complete board (NFR-PERF-002).

## Acceptance Criteria

| AC-ID | Priority | Criterion | Expected Result | Verification Method |
|-------|----------|-----------|-----------------|---------------------|
| AC-005 | P0 | Given a workspace with issues across multiple teams/states/providers, when a board query supplies team, workflow state, assignee, priority, project, label, provider, and sync-condition filters, the returned page contains only matching issues. | Result set matches the filter predicate exactly; identical symbolic filter values are accepted across UI, REST, CLI, and MCP fixtures. | Integration — `BoardQueryServiceTests`, cross-channel contract fixture comparison. |
| AC-006 | P0 | Given `limit` values of 0, 1, 100, and 101, and a malformed opaque cursor, when a board query is issued. | `limit` 1 and 100 return a stable ordered page with a valid cursor; `limit` 0, `limit` 101, and the malformed cursor each return `VALIDATION_FAILED` naming the field. | Integration — boundary tests at 0/1/100/101 plus malformed-cursor test. |
| AC-004 | P0 | Given an issue whose current workflow state has no allowed transition to the requested target state, when a transition is requested. | Response is `INVALID_WORKFLOW_TRANSITION` naming current state, requested state, and violated rule; `Issue.Version` is unchanged. | Integration — `IssueServiceTests.RequestTransition_DisallowedTarget_ReturnsInvalidTransition`, asserting error payload and unchanged persisted version. |
| AC-007 | P0 | Given a create or transition mutation submitted with a valid idempotency key, when the identical request is replayed by the same actor. | Exactly one `Issue`/`ActivityEvent` pair exists after both calls; the replay returns the original result and correlation ID. | Integration — `IssueServiceTests.Create_ReplayedWithSameKey_ProducesNoDuplicate`. |
| AC-011 | P0 | Given any successful create, transition, assignment, or comment mutation, when the mutation completes. | Exactly one `ActivityEvent` is recorded with actor, action, timestamp, target, and a before/after summary containing no secret values. | Unit — `IssueServiceTests.Mutate_RecordsSingleActivityEvent`. |
| AC-IBS-111 | P0 | Given a valid phase transition and a registered `PrePhaseChange` hook that allows it, when the transition commits. | The hook receives typed from/to metadata before persistence; one templated activity and one post-commit real-time delta are emitted, and `PostPhaseChange` failure cannot change the response. | Integration — hook ordering and failure-isolation tests. |
| AC-IBS-112 | P0 | Given a `PrePhaseChange` denial or exhausted pre-hook budget, when a phase transition is requested. | No issue, version, or activity mutation persists; the caller receives the denial or `HOOK_BUDGET_EXCEEDED`. | Integration — transaction rollback/budget tests. |
| AC-IBS-113 | P0 | Given an issue with comments, activities, artifacts, links, and external links, when it is archived then queried normally and with `includeArchived=true`. | Archive is idempotent and zero-cascade; normal queries omit it, while the explicit query returns its intact detail. | Integration — archive visibility and preservation fixture. |
| AC-IBS-114 | P1 | Given reciprocal `BLOCKS` link exposure, when either issue detail is read. | The source projects `Blocks`, the target projects `BlockedBy`, and either issue may transition regardless of the other issue's phase. | Integration — dependency projection/non-enforcement test. |
| AC-IBS-115 | P0 | Given a link activity whose target is an issue reference, when timeline data is returned. | API payload includes the template key, parameters, and typed target reference; UI can render a navigable reference without parsing prose. | Contract — activity serialization/rendering fixture. |
| AC-IBS-116 | P0 | Given a provider resync containing additive remote records and a competing local mutable edit, when synchronization runs. | Additive records union merge first; a remaining mutable conflict preserves remote snapshot as `SYNC_CONFLICT` and dashboard resolution applies only an explicit keep-local, apply-remote, or merged result. | Integration — sync merge/conflict resolver tests. |
| AC-IBS-117 | P1 | Given a committed issue mutation, when a subscribed workspace client receives an update. | It receives a compact, versioned row/detail delta sufficient to patch the affected item without a full-board refetch. | Integration — real-time publisher contract test. |
| AC-IBS-101 | P1 | Given a dashboard summary request and a board query using the identical filter, when both are executed against the same data. | The dashboard's per-status/per-source counts equal the board query's matching row count (reconciliation, FR-WRK-004). | Integration — `DashboardServiceTests.Summary_ReconcilesWithBoardQuery`. |
| AC-IBS-102 | P0 | Given a `Post*` lifecycle hook (e.g. `PostPhaseChange`, `PostAddComment`) registered via `IPluginRegistry` that throws on every invocation, when the corresponding issue mutation completes. | The mutation still returns success and its `ActivityEvent` is persisted; the hook exception is logged, never propagated to the caller, and never rolls back the committed mutation. | Unit — `IssueServiceTests.Mutate_PostHookThrows_MutationStillSucceeds` (negative — must NOT propagate). |
| AC-IBS-102b | P0 | Given a `Pre*` lifecycle hook (e.g. `PrePhaseChange`) registered via `IPluginRegistry` that denies its input, when the corresponding mutation is requested. | The mutation does not persist; the caller receives `VALIDATION_FAILED` with the hook-provided reason. | Unit — `IssueServiceTests.Mutate_PreHookDenies_MutationDoesNotPersist` (negative — must deny). |
| AC-IBS-103 | P1 | Given a board query for a workspace the actor is not authorized for, when the query executes. | No issue rows for that workspace are returned or leaked, regardless of matching filters (defense in depth alongside `workspace-authorization`). | Integration — negative authorization-boundary test asserting empty/denied result and no data leakage. |
| AC-IBS-104 | P1 | Given `type="Bug"` on one issue and `type="Defect"` on another in the same workspace, when both are created with no allow-list configured. | Both issues are created successfully; a query filtering `type="Bug"` returns only the first, and `type="Defect"` returns only the second — no validation error for either value (FR-WRK-005). | Unit — `IssueServiceTests.Create_ArbitraryTypeString_Succeeds`. |
| AC-IBS-105 | P1 | Given an issue with `SessionState` unset, when `UpdateSessionStateAsync(title: "Reviewing", description: "Checking AST before continuing implementation")` is called with no `expectedVersion`. | The update succeeds, `Issue.Version` is unchanged, `Issue.UpdatedAt` advances, and both the simple and detail views render the new title/description immediately. | Unit — `IssueServiceTests.UpdateSessionState_DoesNotIncrementVersion`. |
| AC-IBS-106 | P1 | Given a concurrent `RequestTransitionAsync` (with `expectedVersion`) and `UpdateSessionStateAsync` on the same issue. | Both succeed independently; the transition's version check is unaffected by the session-state update, and vice versa (FR-WRK-006). | Integration — `IssueServiceTests.SessionStateUpdate_DoesNotCollideWithConcurrentTransition`. |
| AC-IBS-107 | P1 | Given 10 issues split across 3 workflow states and 2 types, when a list-view query is issued with `groupBy=type` and `orderBy=createdAt`. | Issues are grouped into exactly 2 named groups plus (if any exist) an `"Untyped"` group, each internally ordered newest-first, and group order is identical across repeated identical queries (FR-WRK-007/FR-WRK-008). | Integration — `BoardQueryServiceTests.ListView_GroupByType_StableGroupsAndOrder`. |
| AC-IBS-108 | P1 | Given a root comment C1 on an issue, when `AddCommentAsync(parentCommentId: C1.Id)` is called, and then a second call attempts `AddCommentAsync(parentCommentId: <the reply just created>)`. | The first call succeeds (reply nested under C1); the second call returns `VALIDATION_FAILED` naming "single level of replies only" (FR-WRK-010). | Unit — `IssueServiceTests.AddComment_ReplyToReply_ReturnsValidationFailed`. |
| AC-IBS-109 | P1 | Given an issue with `AssigneeId = NULL`, when `CreateAsync`/`ListAsync`/`RequestTransitionAsync` are exercised. | Every operation succeeds identically to an assigned issue; the issue renders under the `"Unassigned"` list-view bucket with no error or blocked state (FR-WRK-009). | Unit — `IssueServiceTests.UnassignedIssue_AllOperationsSucceed`. |
| AC-IBS-110 | P1 | Given `Issue.CreatedAt` and `Issue.UpdatedAt` for an issue mutated 3 times after creation, when the board/list/detail views render its dates. | `CreatedAt` is identical to the original insert timestamp across all 3 mutations; `UpdatedAt` reflects only the most recent mutation; both are independently exposed and sortable (FR-WRK-008). | Unit — `IssueServiceTests.CreatedAt_NeverChanges_UpdatedAt_TracksLatestMutation`. |

## Error Handling

Every anticipated failure resolves to a §7.7 catalog code; no raw EF Core or provider exception may propagate past this component.

| Condition | Code | HTTP status | Notes |
|---|---:|---|---|
| Missing/malformed `title`, pagination, or idempotency key | `VALIDATION_FAILED` | 400 | Names the invalid/missing field and requirement. |
| `workflowStateId` does not exist for the workspace | `REFERENCED_ENTITY_NOT_FOUND` | 404 | Applies to create and transition. |
| `workflowStateId` exists but is archived for the workspace | `INVALID_WORKFLOW_TRANSITION` | 409 | Applies to create and transition; names the archived state. |
| Requested transition not in the workspace's allowed-transition set | `INVALID_WORKFLOW_TRANSITION` | 409 | Names current state, requested state, violated rule; no version increment. |
| `expectedVersion` does not match persisted `Issue.Version` | `CONCURRENCY_CONFLICT` | 409 | Response supplies current version for refetch/retry. |
| Duplicate `(WorkspaceId, Key)` on issue creation | `RESOURCE_ALREADY_EXISTS` | 409 | UNIQUE constraint translation (tech-design §7.6). |
| Idempotency key reused with a different payload/actor | `IDEMPOTENCY_KEY_REUSED` | 409 | Detected via the automation surface's `IdempotencyRecords`; this component must not apply a second mutation. |
| Query/mutation for a workspace the actor cannot access | `WORKSPACE_ACCESS_DENIED` | 403 | Enforced by `workspace-authorization` upstream of this component; this component never re-derives it independently. |
| `parentCommentId` does not resolve to a comment on the same issue | `REFERENCED_ENTITY_NOT_FOUND` | 404 | Applies to `AddCommentAsync`. |
| `parentCommentId` resolves to a comment that is itself a reply | `VALIDATION_FAILED` | 400 | Names "single level of replies only" (FR-WRK-010). |
| `SessionState` update supplies neither `title` nor `description` | `VALIDATION_FAILED` | 400 | At least one field is required. |
| A named `Pre*` hook denies its proposed mutation | `VALIDATION_FAILED` | 400 | Returns the hook-provided safe reason; no mutation persists. |
| A pre-commit hook exceeds its configured lifecycle budget | `HOOK_BUDGET_EXCEEDED` | 409 | Forced denial; no mutation persists. |
| Resync detects competing mutable local/remote changes after additive union merge | `SYNC_CONFLICT` | 409 | Preserves remote snapshot for dashboard resolution; `SessionState` is excluded. |
| A conflict resolution has an unknown mode or lacks required merged fields | `VALIDATION_FAILED` | 400 | Accepts only keep-local, apply-remote, or a complete explicit merged result. |

## File Structure

```
src/
├── Anvilboard.Application/
│   ├── Issues/
│   │   ├── IssueService.cs              # CRUD, lifecycle-point ownership, archive, conflict resolution, external upsert
│   │   ├── IIssueService.cs              # Public contract for DI/testability
│   │   ├── BoardQueryService.cs          # Filtered/paginated board & list queries, archived visibility control
│   │   └── IBoardQueryService.cs         # Public board query contract
│   ├── Activity/
│   │   └── ActivityTemplateRenderer.cs   # Renders template + typed references with safe fallback
│   └── Dashboard/
│       └── DashboardService.cs           # Dashboard aggregation using the shared filter predicate
├── Anvilboard.Domain/
│   ├── Issue.cs                          # Workflow state, version, archive timestamp, free-form fields/session state
│   ├── Comment.cs                        # Single-level ParentCommentId
│   ├── ActivityEvent.cs                  # Template key, parameters, typed references
│   └── SyncConflict.cs                   # Persisted remote mutable-field snapshot and resolution status
└── Anvilboard.Infrastructure/
    └── Persistence/
        └── Configurations/
            └── IssueConfiguration.cs      # Workflow FK, archive/query indexes, version concurrency mapping
```

## Test Module

**Test file**: `src/Anvilboard.Application.Tests/Issues/IssueServiceTests.cs`

**Test scope**:
- **Unit**: `CreateAsync()` (including arbitrary `type`/`priority` strings), `RequestTransitionAsync()` (legality delegation, sequential pre-hook deny/budget behavior, version increment, post-hook isolation, `CONCURRENCY_CONFLICT` boundary), `AssignAsync()`, `AddCommentAsync()` (root + single-level-reply + reply-to-reply rejection), `UpdateSessionStateAsync()` (version-bypass behavior), archive/unarchive idempotency, activity-template/reference fallback rendering, and `RecordAndDispatchAsync()` post-commit failure isolation.
- **Integration**: `BoardQueryService`/`IssueService` against seeded SQLite — filter combinations (team, workflow state, assignee, priority, type, project, label, provider, sync condition), archived default/opt-in visibility, list-view grouping/ordering (`groupBy`/`orderBy` combinations including `"Untyped"`/`"No priority"`/`"Unassigned"` buckets), pagination boundaries (0/1/100/101, malformed cursor), advisory `Blocks`/`BlockedBy` projection, and `UpsertFromExternalAsync()` additive union merge, mutable conflict persistence, and explicit resolution.
- **Fixtures / Mocks**: in-memory or file-based SQLite `AnvilboardDbContext` seeded with multi-team, multi-workspace data; fake `IWorkflowService`, configurable typed lifecycle hooks, `IRealtimeUpdatePublisher`, and provider payloads with overlapping additive external IDs.

**Test file**: `src/Anvilboard.Application.Tests/Dashboard/DashboardServiceTests.cs`

**Test scope**:
- **Unit**: `GetSummaryAsync()` count computation per status/source/assignee bucket.
- **Integration**: reconciliation test asserting dashboard summary counts equal the equivalent board query's row count for the same filter.
- **Fixtures / Mocks**: same seeded `AnvilboardDbContext` as above, reused across both test files to keep fixture data consistent between board and dashboard assertions.
