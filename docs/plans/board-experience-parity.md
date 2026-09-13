# Implementation Plan: Board Experience Parity — Unified Board Query Surface, Activity Feed & Link Updates

> Feature-level technical design and execution plan for the highest-ranked **open** capability gap
> in Anvilboard: a complete, tested `IBoardQueryService` that **no caller can reach**, an activity
> log that is written on every mutation but never read back, and an issue link that can be created
> and destroyed but never corrected. Canonical chain:
> [`prd.md`](../anvilboard/prd.md) → [`srs.md`](../anvilboard/srs.md) →
> [`tech-design.md`](../anvilboard/tech-design.md) →
> [`issue-board-service.md`](../features/issue-board-service.md) +
> [`issue-linking.md`](../features/issue-linking.md).

## 1. Document Information

| Field | Value |
|---|---|
| Document | Implementation Plan — Board Experience Parity |
| Feature component | [`issue-board-service`](../features/issue-board-service.md) (primary), [`issue-linking`](../features/issue-linking.md) (secondary) |
| Milestone | [`tech-design.md`](../anvilboard/tech-design.md) §16 **M3.5: Extended Ticket Model & List View** and **M4.5: Artifacts & Issue Linking** |
| Audit findings | [`audit-report.md`](../audit-report.md) **MAJ-007**, **MAJ-008**, **MAJ-010**, **MAJ-011** (priority action #8) |
| SRS refs | `FR-WRK-001` (primary), `FR-WRK-007` (primary), `FR-LNK-001` (primary), `FR-WRK-008` (touched), `FR-WRK-013` (touched), `FR-WRK-014` (touched), `FR-AUT-001` (touched), `NFR-USB-001` (touched), `NFR-SEC-002` (touched) |
| Acceptance criteria | `FR-WRK-001 AC1`–`AC4`, `FR-WRK-007 AC1`–`AC4`, `FR-LNK-001 AC1`, `FR-LNK-001 AC2` |
| Status | **Implemented** — T1–T19 complete; 521 .NET tests and 44 Angular tests passing |
| Created | 2026-09-14 |
| Completed | 2026-09-14 |

## 2. Why this feature was selected

The backlog lives in the feature index and the audit report, not in GitHub issues — the same
rationale recorded in [`artifact-service.md`](./artifact-service.md) §2,
[`workflow-admin-surface.md`](./workflow-admin-surface.md) §2 and
[`integration-sync-health.md`](./integration-sync-health.md) §2 still holds. Selecting "the next
feature" therefore means selecting the highest-ranked verified gap.

| Signal | Finding |
|---|---|
| Open GitHub issues | `gh issue list --state open` → only #41 (Renovate Dependency Dashboard). No feature work is tracked there; no open pull requests. |
| Unresolved Critical findings | **None.** CRIT-001, CRIT-002 and CRIT-003 are all marked **RESOLVED**. |
| Highest open priority action | [`audit-report.md`](../audit-report.md) "Recommended Priority Actions" **#8** — *"Close remaining UI/UX gaps (board filter parity, issue-detail activity feed, link-update endpoint) — fixes MAJ-007, MAJ-008, MAJ-010, MAJ-011 — medium"*. Actions 1–7 and 10 are struck through; **8 is the first open row**. |
| Priority | `FR-WRK-001` is **P0** in a **P0** component, and `FR-WRK-007` is **P1**. The only remaining open action after this one (#9, `FR-OPS-001` audit query, MAJ-018) is explicitly sized "small" and serves a P1 component. |
| Severity of the headline gap | This is not "the UI lags the backend". `IBoardQueryService` has **zero call sites** outside its own tests (§2.1) — the P0 capability is verifiably unreachable from *every* surface: REST, CLI, MCP and web. It is dead code at the composition root. |
| Milestone status | **M3.5** and **M4.5** are the two remaining `Partial` milestones whose §16 status text names exactly these gaps ("the web list view only groups by status", "no link-update endpoint"). M3 stays `Partial` on MAJ-018, which is action #9, not this one. |
| Cost of co-delivery | The four findings converge on the same three files — [`IssueEndpoints.cs`](../../src/Anvilboard.Api/Endpoints/IssueEndpoints.cs), [`board-api.service.ts`](../../src/anvilboard-web/src/app/core/board-api.service.ts), and the two board components. Splitting them into four plans would pay the same integration cost four times. |

### 2.1 Verified current state

Every row below was confirmed by reading the code in this worktree at commit `712c55c`, not
inferred from the specs. The .NET suite is green at this commit (`dotnet test Anvilboard.slnx`,
exit 0, 443 passing).

| Claim | Evidence |
|---|---|
| `IBoardQueryService` is registered but never called | A reference search resolves to exactly two sites: its own implementation [`BoardQueryService.cs:9`](../../src/Anvilboard.Application/Issues/BoardQueryService.cs) and the DI line in `ServiceCollectionExtensions.cs:33`. No endpoint, no agent operation, and no test outside `BoardQueryServiceTests.cs` touches it. |
| The capability behind it is complete | [`IBoardQueryService.cs`](../../src/Anvilboard.Application/Issues/IBoardQueryService.cs) declares `BoardQuery` with `WorkflowStateId`, `AssigneeId`, `Provider`, `ProjectId`, `Priority`, `Type`, `LabelId`, `SyncCondition`, `GroupBy`, `OrderBy`, `Page`, `Limit`, `Cursor`, `IncludeArchived` — and `BoardQueryResult` already echoes `AppliedQuery` back, which is literally `FR-WRK-001 AC1`. |
| The route users actually hit is impoverished | [`IssueEndpoints.cs:22`](../../src/Anvilboard.Api/Endpoints/IssueEndpoints.cs) — `GET /api/issues` accepts only `Guid? teamId, IssueStatus? status, Guid? assigneeId` and delegates to `IssueService.ListAsync`. Six of the fourteen `BoardQuery` inputs have no HTTP expression at all. |
| …and it sorts in memory | `IssueService.ListAsync` ([`IssueService.cs:48`](../../src/Anvilboard.Application/Issues/IssueService.cs)) materializes before ordering, with a comment noting SQLite's EF provider cannot translate the sort. `BoardQueryService` has the same constraint but at least paginates deterministically. |
| The web board is status-only | `board-page.ts` builds `columns` by mapping `ISSUE_STATUSES`; `board-page.html`'s toolbar contains a single **Refresh** button and no filter, group, or order control. |
| …and the client could not send more if it wanted to | `board-api.service.ts` `listIssues` (line 41) builds its query string from `teamId`/`status`/`assigneeId` only. |
| No activity read path exists anywhere | A route inventory across all nine `*Endpoints.cs` files finds no activity path. `ActivityEvent` rows are written by `IssueService`, `IssueLinkService` and `ArtifactService`, indexed on `IssueId` and `OccurredAt` (`ActivityEventConfiguration.cs`), and then never read. |
| Comments are equally write-only | `IssueEndpoints.cs:115` maps `POST /{id}/comments`; there is **no** `GET`. `issue-detail.ts` appends the created comment to a local signal, so reloading the page empties the panel. |
| Links cannot be updated | `IssueLinkService` exposes `CreateLinkAsync` (line 14), `ListLinksAsync` (71) and `RemoveLinkAsync` (87) — no update. REST mirrors that exactly: `GET`/`POST /{id}/links` and `DELETE /{id}/links/{linkId}`. `FR-LNK-001 AC2` names *"creating, listing, **updating the type/description**, and removing"*. |
| Link types are invented by the client | [`issue-detail.ts:13`](../../src/anvilboard-web/src/app/board/issue-detail/issue-detail.ts) hardcodes `SUGGESTED_LINK_TYPES = ['RELATED', 'PARENT', 'DUPLICATE', 'MENTIONED_IN', 'BLOCKS']`, rendered as a `<datalist>`. The server has no opinion; the CLI/MCP surface suggests nothing at all. |
| `ActivityEventType` has no "link updated" member | The enum in `ActivityEvent.cs` runs Created … `IssueLinkCreated`, `IssueLinkRemoved`, `ArtifactAttached` … — an update would currently have to lie about its own type. |
| `RestWorkspaceScope` cannot scope a project or a label | It offers `RequireTeamAsync`, `RequireIssueAsync`, `RequireMemberAsync`, `RequireWorkflowStateAsync`, `RequireWorkflowTransitionAsync` — and nothing for `ProjectId`/`LabelId`, both of which the board query filters on. |
| Nothing lists projects or labels over HTTP | No endpoint file references `db.Projects` or `db.Labels`. A filter UI offering those pickers has no data source today. |
| The realtime layer already expects an activity consumer | `board-page.ts.applyChange` deliberately ignores `REALTIME_ACTIVITY_ADDED` with the comment *"Activity is consumed by issue-detail surfaces"* — a surface that does not exist yet. |

## 3. Overview

### 3.1 Background

Anvilboard's board promise is one authorized query rendered two ways: *"filtering and grouping by
team, workflow state, assignee, priority, project, label, provider, and synchronization
condition"* (`FR-WRK-001`), surfaced as both a kanban and a denser list view that *"never diverge in
which issues are included"* (`FR-WRK-007 AC1`).

Roughly ninety percent of that is built. `BoardQueryService` filters on all eight dimensions,
groups five ways, orders four ways, paginates with a cursor, resolves `syncCondition` through
`IIntegrationHealthService` so the board and the dashboard can never disagree, and echoes the
applied query back in its result. It has its own test file. It is wired into DI.

And it has no callers. The only path a human or agent can take into the board is
`GET /api/issues`, which understands three filters and returns a flat list. The richest thing the
product can do is the thing none of its users can ask for. That is a strictly worse failure than an
unbuilt feature: the cost has already been paid and the value is zero, and every day the dead
interface stays dead it drifts further from the endpoint that will eventually have to expose it.

The same shape repeats twice more, smaller. Activity events are recorded faithfully on every
mutation, indexed for exactly the query a feed would issue — and never read. Issue links can be
created and deleted but not corrected, so fixing a typo in a link description means destroying the
link and its provenance and making a new one, which also destroys the `IssueLinkRemoved` /
`IssueLinkCreated` pair's meaning in the very activity log we are about to start showing people.

This plan closes the read and edit paths that make the existing write paths worth having.

### 3.2 Goals

1. **G1** — Expose `IBoardQueryService` over REST as `GET /api/board`, with every `BoardQuery`
   dimension addressable and every applied filter echoed in the response
   (`FR-WRK-001 AC1`–`AC4`, MAJ-007).
2. **G2** — Give the web board a filter/group/order control surface driven by that route, and make
   the kanban and list renderings two views of the one query (`FR-WRK-007 AC1`–`AC4`, MAJ-007).
3. **G3** — Add an activity read path (`GET /api/issues/{id}/activity`) and render it as an
   issue-detail feed that also consumes `REALTIME_ACTIVITY_ADDED` (`FR-WRK-013`, `FR-WRK-014`,
   MAJ-008).
4. **G4** — Add `PATCH /api/issues/{id}/links/{linkId}` over a new
   `IssueLinkService.UpdateLinkAsync`, with a new `ActivityEventType.IssueLinkUpdated` and the
   uniqueness invariant re-checked on type change (`FR-LNK-001 AC2`, MAJ-010).
5. **G5** — Make the link-type vocabulary server-sourced so web, CLI and MCP suggest one list,
   still without rejecting an unlisted value (`FR-LNK-001 AC1`, MAJ-011).
6. **G6** — Mirror every new read/write on the agent surface, preserving the one-to-one
   REST ↔ CLI/MCP invariant that `AGENTS.md` and `BoardAgentService` both assert.

### 3.3 Non-Goals

- **Threaded comments** (MAJ-006). The domain has no parent-comment reference; adding one is a
  schema change with its own migration and its own spec correction. The activity feed renders
  `CommentAdded` events flat, exactly as the data is.
- **Optimistic-concurrency enforcement** (MAJ-009). `Issue.Version` is inconsistently applied
  across mutation paths. This plan adds no new issue-field mutation, so it neither fixes nor
  worsens that; it stays action-scoped to its own finding.
- **Workspace-scoped audit query** (MAJ-018, `FR-OPS-001`). That is priority action #9 and belongs
  to `audit-and-recovery`. Activity events are **not** audit events, and this plan touches only the
  former.
- **Retiring `GET /api/issues`.** The legacy route and its `IssueStatus` enum stay, unchanged and
  additionally deprecated in OpenAPI, for exactly one release. See `DR-BXP-002`.
- **Saved views / shareable board URLs.** Filter state is held in the component and reflected in
  query parameters for reload-survival; persisting named views is a separate feature.
- **Structured activity templates** (`FR-WRK-013 AC1`–`AC2` in full). This plan exposes the
  `DataJson` already recorded plus a flat rendered fallback; designing the template vocabulary is a
  follow-up (§13.2).

### 3.4 Scope

**In:** a new `GET /api/board` route and its request/response contract; `RequireProjectAsync` /
`RequireLabelAsync` on `RestWorkspaceScope`; `GET /api/projects` and `GET /api/labels` list routes
to feed the filter pickers; `GET /api/issues/{id}/activity`; `GET /api/issues/{id}/comments`;
`IssueLinkService.UpdateLinkAsync` + `PATCH /api/issues/{id}/links/{linkId}`;
`ActivityEventType.IssueLinkUpdated`; a server-sourced link-type vocabulary; five agent operations;
Angular models, API-client methods, board filter/group/order UI, list-view rendering, issue-detail
activity feed and link editing; tests per §12; documentation per §13.1.

**Out:** everything in §3.3; any change to `BoardQueryService`'s filtering semantics (it is correct
and tested — this plan reaches it, it does not rewrite it); any new persisted column or migration.

### 3.5 User Scenarios

1. **Triage by provider freshness.** A coordinator opens the board, groups by assignee, filters to
   `provider=GITHUB` and `syncCondition=STALE`, and orders by priority. The board shows only work
   whose upstream has gone quiet, grouped by who owns it. Today none of those four controls exists.
2. **"Why is this Done?"** A contributor opens an issue and reads its activity feed: created,
   assigned, moved to In Review, commented, moved to Done — with the actor and timestamp on each
   row. Today the panel does not exist and even the comments vanish on reload.
3. **Correcting a link.** Someone linked `COM-234` as `RELATED` when it is really `BLOCKS`. They
   edit the link in place; the type changes, the description is kept, and an `IssueLinkUpdated`
   event lands in both issues' feeds. Today the only route is delete-and-recreate, which rewrites
   history and loses `CreatedAt`.
4. **Agent parity.** An automation calls `query-board` with the same filter set the human used and
   gets the same grouped result, because both call `IBoardQueryService` once.
5. **Empty is a real answer.** A filter combination that matches nothing returns `200` with zero
   groups, `totalCount: 0`, and the applied filters echoed — so the UI can say *"no issues match
   provider GITHUB + condition STALE"* rather than showing an ambiguous blank column
   (`FR-WRK-001 AC3`, `NFR-USB-001`).

### 3.6 Acceptance Criteria

| ID | Criterion | Traces to |
|---|---|---|
| `AC-BXP-001` | `GET /api/board` accepts every `BoardQuery` dimension — `workflowStateId`, `assigneeId`, `provider`, `projectId`, `priority`, `type`, `labelId`, `syncCondition`, `groupBy`, `orderBy`, `page`, `limit`, `cursor`, `includeArchived` — and reaches `IBoardQueryService.QueryAsync` with them. | `FR-WRK-001 AC1`, MAJ-007 |
| `AC-BXP-002` | The response echoes the applied query back to the caller, including defaults the caller did not send. | `FR-WRK-001 AC1` |
| `AC-BXP-003` | A caller-supplied `teamId`, `assigneeId`, `projectId`, `labelId` or `workflowStateId` belonging to another workspace returns `403 WORKSPACE_ACCESS_DENIED`, byte-identical to an unknown id. | `FR-WRK-001 AC2`, `NFR-SEC-002` |
| `AC-BXP-004` | A filter combination matching no issue returns `200` with `groups: []`, `totalCount: 0`, and the applied query — never `404`, never an error. | `FR-WRK-001 AC3`, `NFR-USB-001` |
| `AC-BXP-005` | `page` < 1 or `limit` outside 1–100 returns `400 VALIDATION_FAILED`; paging the same unchanged data twice yields the same order and the same `nextCursor`. | `FR-WRK-001 AC4` |
| `AC-BXP-006` | The web board's kanban and list renderings issue the identical `GET /api/board` request and differ only in presentation. | `FR-WRK-007 AC1`, MAJ-007 |
| `AC-BXP-007` | The board UI offers grouping by workflow state, type, priority, assignee and label, and ordering by created, updated and priority in both directions. | `FR-WRK-007 AC2`, `AC3`, `FR-WRK-008` |
| `AC-BXP-008` | Switching between kanban and list preserves the active filter, grouping and ordering. | `FR-WRK-007 AC4` |
| `AC-BXP-009` | `GET /api/issues/{id}/activity` returns that issue's events newest-first with type, actor, `occurredAt`, structured `data` and a flat rendered fallback, paginated. | `FR-WRK-013`, MAJ-008 |
| `AC-BXP-010` | The issue-detail view renders that feed and appends live on `REALTIME_ACTIVITY_ADDED` without a refetch. | `FR-WRK-014`, MAJ-008 |
| `AC-BXP-011` | `GET /api/issues/{id}/comments` returns persisted comments, and the detail panel survives a page reload. | `FR-WRK-002`, MAJ-008 |
| `AC-BXP-012` | `PATCH /api/issues/{id}/links/{linkId}` updates `type` and/or `description`, preserves `Id`/`CreatedAt`/`CreatedById`, and records `ActivityEventType.IssueLinkUpdated` on both issues. | `FR-LNK-001 AC2`, MAJ-010 |
| `AC-BXP-013` | A type change that would collide with an existing `(source, target, type)` link returns `409 RESOURCE_ALREADY_EXISTS` and writes nothing. | `FR-LNK-001 AC2` |
| `AC-BXP-014` | Updating a link on an issue outside the caller's workspace returns `403 WORKSPACE_ACCESS_DENIED`; a `linkId` that exists but belongs to a different issue is denied the same way. | `NFR-SEC-002` |
| `AC-BXP-015` | The suggested link-type vocabulary is served by the API, consumed by the web datalist and reported by the agent surface; an unlisted value is still accepted. | `FR-LNK-001 AC1`, MAJ-011 |
| `AC-BXP-016` | Agent operations `query-board`, `list-issue-activity`, `list-issue-comments`, `update-issue-link` and `list-link-types` exist with permissions matching their REST twins. | `FR-AUT-001`, G6 |
| `AC-BXP-017` | Every new route appears in `CrossWorkspaceIsolationEndpointTests` and passes. | `NFR-SEC-002` |

### 3.7 Success Metrics

- `IBoardQueryService` reference count goes from 2 (impl + DI) to ≥ 4 with at least one REST and
  one agent caller — the dead-code condition in §2.1 is structurally undone.
- Board filter dimensions reachable from the web UI: 3 → 8.
- Activity events readable through any surface: 0 → all.
- `docs/audit-report.md` MAJ-007, MAJ-008, MAJ-010 and MAJ-011 all move to **RESOLVED**; priority
  action #8 closes.
- Suite stays green; .NET count rises from 443 and the Angular count from 21.

## 4. System Context

```mermaid
graph TD
    subgraph Web[anvilboard-web]
        BP[board-page<br/>filters · group · order<br/>kanban ⇄ list]
        ID[issue-detail<br/>activity feed · link edit]
        API[BoardApiService]
        RT[realtime-board-sync]
    end

    subgraph Api[Anvilboard.Api]
        BE[BoardEndpoints<br/>GET /api/board]
        IE[IssueEndpoints<br/>+activity +comments +PATCH link]
        TE[TaxonomyEndpoints<br/>projects · labels · link-types]
        RWS[RestWorkspaceScope<br/>+project +label]
    end

    subgraph App[Anvilboard.Application]
        BQ[BoardQueryService]
        IS[IssueService]
        ILS[IssueLinkService<br/>+UpdateLinkAsync]
        AS[ActivityQueryService<br/>new]
        IHS[IntegrationHealthService]
    end

    AG[BoardAgentService<br/>query-board · list-issue-activity<br/>update-issue-link · list-link-types]
    DB[(SQLite:<br/>Issues · ActivityEvents<br/>IssueLinks · Labels · Projects)]

    BP --> API
    ID --> API
    RT -.issue.changed.-> BP
    RT -.activity.added.-> ID
    API --> BE
    API --> IE
    API --> TE
    BE --> RWS
    IE --> RWS
    TE --> RWS
    BE --> BQ
    IE --> IS
    IE --> ILS
    IE --> AS
    AG --> BQ
    AG --> AS
    AG --> ILS
    BQ --> IHS
    BQ --> DB
    AS --> DB
    ILS --> DB
```

The shape to notice: **`BoardQueryService` gains two inbound arrows and loses none of its
internals.** The whole board half of this plan is adapter work over a service that is already
correct. `ActivityQueryService` is the only new application-layer type, and it is a read-only
projection over a table that already exists and is already indexed for it.

## 5. Solution Design

The hard question is not the activity feed or the link PATCH — both are conventional. It is **how
the rich board query enters the HTTP surface** without either breaking the route the web app
currently depends on or leaving two divergent list paths behind forever.

### Option A — New `GET /api/board` beside the legacy `GET /api/issues` (Recommended)

Add a purpose-built route whose query string is the `BoardQuery` record, returning
`BoardQueryResult`. Leave `GET /api/issues` byte-identical, mark it deprecated in OpenAPI, and
migrate the web client to `/api/board`. Delete the legacy route in a later release once no caller
remains.

- **+** Zero risk to the current UI, the CLI, and any script already calling `/api/issues`.
- **+** The new route is grouped and paginated from birth; it never has to pretend to be a flat
  list, and `groups`/`nextCursor` are not bolted onto an existing array contract.
- **+** Sidesteps the legacy `IssueStatus`-vs-`workflowStateId` duality entirely: the new route
  speaks `workflowStateId` only, and the six-value enum stays quarantined in the old route. This is
  the deliberate residual MIN-003 signed off on — "the deprecated `Issue.Status` projection remains
  for compatibility with existing board/dashboard views" ([audit-report.md](../audit-report.md)
  line 414) — and Option A shrinks rather than widens it.
- **−** Two list paths coexist for a release, and `IssueService.ListAsync` keeps its in-memory sort.
- **−** Requires an explicit deprecation follow-through, or the duality becomes permanent.

### Option B — Retrofit `GET /api/issues` into the full board query

Extend the existing route with all fourteen parameters and change its response from `Issue[]` to
`BoardQueryResult`.

- **+** One path, immediately. No deprecation debt.
- **−** **Breaking.** The response type changes shape for every existing caller, including
  `issue-detail.ts`, which calls `listIssues()` purely to populate the link-target picker.
- **−** Forces an immediate answer on the legacy `status` filter — either the route takes both `status` and
  `workflowStateId` (confusing) or it drops `status` (breaking twice).
- **−** Couples a large UI change to a large API change in one step, with no intermediate state
  where the suite is meaningfully green.

### Option C — Keep the API as-is; filter and group in the browser

Fetch all issues with `GET /api/issues` and implement the full filter/group/order stack in
`board-page.ts`.

- **+** No backend change at all; fastest to a demo.
- **−** Leaves `IBoardQueryService` dead, so **MAJ-007's actual finding is untouched** — the gap is
  that the backend capability is unreachable, not that the UI lacks buttons.
- **−** Violates `FR-WRK-001 AC4`: pagination and deterministic ordering cannot be honored by a
  client that has already downloaded everything.
- **−** Cannot implement `syncCondition` at all — that filter is derived server-side from
  `IIntegrationHealthService`.
- **−** Reproduces business rules in TypeScript that already exist in C#, the precise divergence
  `IssueEndpoints`' own class comment says the architecture exists to prevent.

### Comparison

| Criterion | A (new route) | B (retrofit) | C (client-side) |
|---|---|---|---|
| Closes MAJ-007 as written | Yes | Yes | **No** |
| Breaking for existing callers | No | **Yes** | No |
| Honors `FR-WRK-001 AC4` (paging/order) | Yes | Yes | **No** |
| Supports `syncCondition` | Yes | Yes | **No** |
| Business logic stays single-sourced | Yes | Yes | **No** |
| Forces the legacy `status` filter to be resolved now | No | Yes | No |
| Leaves deprecation debt | One route, one release | None | None |
| Estimated size | Medium | Large | Small |

### Decision

**Option A.** It is the only option that closes the finding without a breaking change, and the
deprecation debt it creates is bounded, written down (`DR-BXP-002`), and strictly smaller than the
coupled-migration risk of B. C is rejected outright: it would put buttons on a board while leaving
the dead interface dead, which is the failure mode the audit finding actually describes.

## 6. Architecture

Five seams, in dependency order:

1. **Domain** — one additive enum member, `ActivityEventType.IssueLinkUpdated`. No new entity, no
   new column, no migration. (Appending to the enum is safe: `ActivityEventConfiguration` persists
   it by its declared type, and every existing member keeps its ordinal.)
2. **Application/Issues** — `IssueLinkService.UpdateLinkAsync` (the one new write path in this
   plan) and a new read-only `ActivityQueryService` + `IActivityQueryService`. `BoardQueryService`
   is **not** modified.
3. **Api/Authorization** — `RestWorkspaceScope` gains `RequireProjectAsync` and `RequireLabelAsync`,
   following the exact shape of `RequireTeamAsync`: resolve, check workspace membership, throw
   `WorkspaceScopeDeniedException` on either miss so unknown and foreign are indistinguishable.
4. **Api/Endpoints** — a new `BoardEndpoints` (one route), a new `TaxonomyEndpoints` (projects,
   labels, link types), and three additions to `IssueEndpoints`. Every raw `Guid` in a query string
   or route goes through seam 3 before it reaches an application service — no exceptions.
5. **Agent + Web** — five `[AgentOperation]` wrappers and the Angular work: models, client methods,
   board controls, list view, activity feed, link editing.

Consistent with `DR-WFA-001`, **the application service owns its own activity emission**:
`UpdateLinkAsync` writes its `IssueLinkUpdated` events inside the same `SaveChangesAsync` as the
mutation, exactly as `CreateLinkAsync` and `RemoveLinkAsync` already do. Endpoints never write
activity.

`ActivityQueryService` is deliberately a separate type from `IssueService` rather than another
method on it. `IssueService` is the write path and is already large; activity read is a projection
with different caching characteristics and a different permission (`ReadBoard`, not
`ReadWriteIssues`), and keeping it separate means the feed can never accidentally acquire a
mutation.

## 7. Stack & Conventions

### 7.1 Stack

Unchanged: .NET 10, EF Core over SQLite, Minimal APIs, xUnit; Angular standalone components with
signals, Karma/Jasmine. **No new package** on either side.

### 7.2 Naming

| Thing | Convention | This plan |
|---|---|---|
| REST route | lower-kebab plural | `/api/board`, `/api/labels`, `/api/issues/{id}/activity`, `/api/issues/{id}/links/{linkId}` |
| Query parameter | lowerCamelCase, mirroring the record property | `workflowStateId`, `syncCondition`, `groupBy`, `includeArchived` |
| Agent operation | lower-kebab, verb-first | `query-board`, `list-issue-activity`, `list-issue-comments`, `update-issue-link`, `list-link-types` |
| Error code | UPPER_SNAKE, §7.7 catalog only | `VALIDATION_FAILED`, `RESOURCE_ALREADY_EXISTS`, `REFERENCED_ENTITY_NOT_FOUND`, `WORKSPACE_ACCESS_DENIED` |
| DTO symbolic value | UPPER_SNAKE | `syncCondition: "STALE"`, `groupBy: "ASSIGNEE"`, `type: "ISSUE_LINK_UPDATED"` |
| Domain enum | PascalCase | `ActivityEventType.IssueLinkUpdated` |
| Angular component | Angular 17+ flat naming | `board-filters.ts`, `activity-feed.ts` |

### 7.3 Parameter Validation

`GET /api/board` takes no `workspaceId` — it comes from the authenticated actor, as with
`/api/integrations/health`, which removes an entire class of cross-workspace probe. Every other id
in the query string is resolved through `RestWorkspaceScope` **before** the `BoardQuery` record is
constructed, so an unauthorized id never reaches the application layer at all.

`page`/`limit` bounds are **not** re-validated in the endpoint: `BoardQueryService.Validate` already
owns them and throws `BoardQueryException`. The endpoint's only job is to translate that into a
`400 VALIDATION_FAILED` problem response. Duplicating the bound check would create two places to
change it.

Unparseable `priority` and `type` values are *not* errors — `BoardQueryService` treats an
unmatched `priority` as "matches nothing" and `type` as a `LIKE` pattern. The endpoint preserves
that: a nonsense filter yields the empty-but-successful response of `AC-BXP-004`.

`UpdateLinkAsync` requires at least one of `type`/`description` to be present, rejects a whitespace-
only `type` (matching `CreateLinkAsync`), and treats an absent field as "leave unchanged" rather
than "set to null" — a `PATCH`, not a `PUT`.

### 7.4 Boundary Values

| Value | Bound | Rationale |
|---|---|---|
| `limit` | 1–100, default 25 | Existing `BoardQueryService` constants; the contract inherits them rather than redefining them |
| `page` | ≥ 1 | Existing `Validate` |
| Activity page size | default 50, max 200 | A busy issue's whole history is small; one page usually suffices, and the cap keeps a pathological issue from being a DoS |
| Comment page size | default 50, max 200 | Same shape as activity, deliberately identical so the two panels page alike |
| Link `type` | non-empty after trim, ≤ 64 chars | Matches `CreateLinkAsync`; the length cap is new and prevents a free-form field becoming a blob |
| Link `description` | ≤ 1024 chars | Same reasoning |
| Group count | bounded by `limit` | Grouping happens after paging in `BoardQueryService`, so the group count can never exceed the page size |

### 7.5 Business Rules

1. **One query, two renderings.** The kanban and list views call `GET /api/board` with an identical
   parameter set. Neither filters client-side. This is `FR-WRK-007 AC1` enforced by construction.
2. **Empty is success.** Zero matches → `200`, `groups: []`, `totalCount: 0`, `appliedQuery`
   populated. The UI renders the active filters in the empty state.
3. **Link update preserves identity.** `Id`, `SourceIssueId`, `TargetIssueId`, `CreatedById` and
   `CreatedAt` are immutable. Only `Type` and `Description` change.
4. **Uniqueness is re-checked on type change.** `CreateLinkAsync` enforces one link per
   `(source, target, type)`. A `PATCH` that changes `Type` must re-run that check against the new
   type, excluding the row being edited. A `PATCH` that changes only `Description` skips it.
5. **Activity is append-only.** The new read path adds no mutation or deletion of `ActivityEvent`.
6. **Suggestion, never rejection.** The link-type vocabulary is advisory. `FR-LNK-001 AC1` requires
   an unlisted value to be accepted; the server continues to validate only non-emptiness.
7. **Both sides see the update.** `UpdateLinkAsync` records `IssueLinkUpdated` against both the
   source and the target issue, matching `CreateLinkAsync`'s existing two-event behavior, so the
   feed on either issue tells the whole story.

### 7.6 Concurrency

Link update uses EF's tracked-entity update inside one `SaveChangesAsync`; the uniqueness re-check
and the write are in the same transaction. There is no `Version` token on `IssueLink` and this plan
does not add one (MAJ-009 is out of scope) — last write wins on a link, which is acceptable for a
two-field advisory record and is stated rather than assumed.

Board and activity reads are `AsNoTracking` snapshots. A page fetched during a concurrent mutation
may miss or repeat a row across page boundaries; the cursor is an encoded offset, not a keyset.
That limitation is inherited from `BoardQueryService`, not introduced here, and is recorded as
`DR-BXP-004`.

### 7.7 Error Handling

New endpoints map exceptions through
[`ErrorCodeCatalog.HttpStatusFor`](../../src/Anvilboard.Application/Automation/ErrorCodeCatalog.cs)
rather than repeating the hand-written `switch` that `POST /{id}/links` currently carries. The
catalog is the §7.7 mirror and already knows `VALIDATION_FAILED` → 400,
`REFERENCED_ENTITY_NOT_FOUND` → 404, `RESOURCE_ALREADY_EXISTS` → 409, `CONCURRENCY_CONFLICT` → 409,
unknown → 500.

`WorkspaceScopeDeniedException` keeps its existing dedicated path to `WorkspaceScopeResults.Denied()`
(403 `WORKSPACE_ACCESS_DENIED`) — it is an authorization outcome, not a business error, and must
never vary by cause.

## 8. Detailed Design

### 8.1 `src/Anvilboard.Domain/ActivityEvent.cs` (modified)

Add one member to `ActivityEventType`. It **must be appended last, after `ArtifactRemoved`** — not
placed next to the other `IssueLink*` members where it would read more naturally:

```csharp
    ArtifactAttached,
    ArtifactRemoved,
    IssueLinkUpdated,   // new — must stay last
```

`ActivityEventConfiguration` declares no `HasConversion` for `Type`, so EF persists the enum as its
underlying `int`. Inserting the member mid-enum would silently renumber every already-persisted
`Artifact*` row in every existing database. This is the single most important detail in the file and
is recorded as `DR-BXP-005`.

### 8.2 `src/Anvilboard.Application/Issues/IssueLinkService.cs` (modified)

```csharp
public async Task<IssueLinkDto> UpdateLinkAsync(
    WorkspaceId workspaceId,
    IssueId issueId,
    IssueLinkId linkId,
    string? type,
    string? description,
    MemberId? actorId,
    CancellationToken ct = default)
```

Behavior, in order: require the issue in workspace (existing helper); load the link and require it
to reference `issueId` on either side (otherwise `IssueLinkException` with
`REFERENCED_ENTITY_NOT_FOUND`); require both endpoints still in workspace; reject an all-null patch
and a whitespace `type` with `VALIDATION_FAILED`; if `type` changes, re-run the
`(source, target, type)` uniqueness query excluding `linkId` and throw `RESOURCE_ALREADY_EXISTS` on
a hit; apply the changed fields; record `IssueLinkUpdated` against both issues via the existing
`RecordActivityAsync`, with `DataJson` carrying `{ "from": {...}, "to": {...} }`; save once; return
`IssueLinkDto.FromLink`.

### 8.3 `src/Anvilboard.Application/Activity/ActivityQueryService.cs` (new)

```csharp
public interface IActivityQueryService
{
    Task<ActivityPage> ListForIssueAsync(
        WorkspaceId workspaceId, IssueId issueId, int limit = 50, string? cursor = null,
        CancellationToken ct = default);
}

public sealed record ActivityEntryDto(
    Guid Id, string Type, Guid? ActorId, string? ActorDisplayName,
    DateTimeOffset OccurredAt, JsonElement? Data, string Text);

public sealed record ActivityPage(IReadOnlyList<ActivityEntryDto> Entries, string? NextCursor);
```

Scoping joins `ActivityEvents → Issues → Teams` and filters on `Teams.WorkspaceId`, the same join
`BoardQueryService` uses. Ordering is `OccurredAt` descending, then `Id` ascending, so the sort is
total and the offset cursor is stable. `Text` is a server-rendered flat fallback
(`FR-WRK-013 AC3`); `Data` is the stored `DataJson` parsed, or `null`. Actor display names are
resolved with one batched `Members` lookup, not per row.

### 8.4 `src/Anvilboard.Api/Authorization/RestWorkspaceScope.cs` (modified)

Two new methods, structurally identical to `RequireTeamAsync`:

```csharp
public async Task<ProjectId> RequireProjectAsync(Guid projectId, CancellationToken ct = default);
public async Task<LabelId> RequireLabelAsync(Guid labelId, CancellationToken ct = default);
```

`Label` carries `WorkspaceId` directly. `Project` does **not** — it hangs off `TeamId`, so
`RequireProjectAsync` joins `Projects → Teams` and compares `Teams.WorkspaceId`. Both throw
`WorkspaceScopeDeniedException` for unknown and foreign alike. Nullable overloads mirror
`RequireMemberAsync` so an absent filter is a no-op.

### 8.5 `src/Anvilboard.Api/Endpoints/BoardEndpoints.cs` (new)

One route in one group:

```csharp
var group = app.MapGroup("/api/board").WithTags("Board").RequirePermission(Permission.ReadBoard);

group.MapGet("/", async (
    [AsParameters] BoardQueryRequest request,
    IBoardQueryService service, RestWorkspaceScope scope, CancellationToken ct) => { … });
```

`BoardQueryRequest` is a flat record of nullable primitives (`Guid?`, `string?`, `int?`, `bool?`),
bound by `[AsParameters]`. The handler resolves each id through `scope`, parses the symbolic enums
case-insensitively, applies defaults for `groupBy`/`orderBy`/`page`/`limit`, builds `BoardQuery`,
calls `QueryAsync`, and projects to the §9.2 DTO. `BoardQueryException` → 400 `VALIDATION_FAILED`;
`WorkspaceScopeDeniedException` → `WorkspaceScopeResults.Denied()`. An unrecognized `groupBy` /
`orderBy` / `syncCondition` / `provider` token is a `400 VALIDATION_FAILED` naming the parameter —
unlike `priority`/`type`, these are closed vocabularies where a typo is a caller bug, not a filter.

Register in `Program.cs` beside `MapIssueEndpoints`.

### 8.6 `src/Anvilboard.Api/Endpoints/IssueEndpoints.cs` (modified)

Three additions to the existing group:

| Addition | Permission |
|---|---|
| `GET /{id:guid}/activity` → `IActivityQueryService` | inherits `ReadBoard` |
| `GET /{id:guid}/comments` → `IssueService` | inherits `ReadBoard` |
| `PATCH /{id:guid}/links/{linkId:guid}` → `IssueLinkService.UpdateLinkAsync` | `.RequirePermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)` |

All three resolve `id` (and `linkId`) through `RestWorkspaceScope` first. The `PATCH` mirrors the
existing `DELETE /{id}/links/{linkId}` for scoping and the existing `POST /{id}/links` for its
request body shape, with both fields optional.

`IssueService` gains `ListCommentsAsync(workspaceId, issueId, limit, cursor, ct)` — a read-only
sibling of the existing `AddCommentAsync`, scoped identically.

### 8.7 `src/Anvilboard.Api/Endpoints/TaxonomyEndpoints.cs` (new)

Three read-only routes, all `ReadBoard`, all workspace-scoped from the actor with no caller id:

- `GET /api/projects` → `{ id, teamId, name, description, targetDate }[]`
- `GET /api/labels` → `{ id, name, color }[]`
- `GET /api/issue-link-types` → `{ types: ["RELATED", "PARENT", …], enforced: false }`

The first two exist solely so the board filter pickers have a data source (§2.1: nothing lists them
today). The third is the single source for `AC-BXP-015`; the vocabulary constant lives in
`Anvilboard.Domain` so the agent surface and the REST surface read the same array and the Angular
`SUGGESTED_LINK_TYPES` constant is deleted.

`enforced: false` is in the payload deliberately — it tells a client that an unlisted value will be
accepted, which is `FR-LNK-001 AC1` expressed in the contract instead of in prose.

### 8.8 `src/Anvilboard.Agent/BoardAgentService.cs` (modified)

Five operations, following the file's existing `[AgentOperation]` + `Ok<T>(…)` shape:

```csharp
[AgentOperation("query-board", "Queries the board with the full filter/group/order set", Category = "issues", IsIdempotent = true)]
[AgentOperation("list-issue-activity", "Lists an issue's activity history, newest first", Category = "issues", IsIdempotent = true)]
[AgentOperation("list-issue-comments", "Lists an issue's comments", Category = "issues", IsIdempotent = true)]
[AgentOperation("list-link-types", "Lists suggested issue-link types (advisory, not enforced)", Category = "issues", IsIdempotent = true)]
[AgentOperation("update-issue-link", "Updates an existing issue link's type and/or description", Category = "issues")]
```

The four read operations join `OperationsWithoutIdempotencyKey` in `AgentCatalogInvariantsTests`.
`update-issue-link` is a mutation and therefore takes an idempotency key like its siblings. Ids
resolve through `AgentWorkspaceScope`, which needs the same project/label additions as §8.4.

### 8.9 `src/anvilboard-web/src/app/core/models.ts` (modified)

Add `BoardGroupBy`, `BoardOrderBy`, `BoardSyncCondition` as string-union types (the new route
speaks symbolic values, not the numeric enums the legacy route uses); `BoardQueryRequest`,
`BoardQueryResult`, `BoardGroup`, `BoardIssue`; `ActivityEntry`; `Project`; `Label`. Keep the
existing numeric enums untouched — they still describe `/api/issues`.

### 8.10 `src/anvilboard-web/src/app/core/board-api.service.ts` (modified)

Six methods, in the file's existing flat style: `queryBoard`, `listIssueActivity`,
`listIssueComments`, `updateIssueLink`, `listProjects`, `listLabels`, `listLinkTypes`.
`listIssues` stays for the link-target picker until the legacy route is retired.

### 8.11 `src/anvilboard-web/src/app/board/board-filters/` (new)

A presentational standalone component: filter selects for workflow state, assignee, provider,
project, label, priority, type and sync condition; a group-by select; an order-by select with a
direction toggle; and a kanban/list view switch. One `filtersChanged` output carrying the whole
`BoardQueryRequest` — the component holds no fetch logic.

### 8.12 `src/anvilboard-web/src/app/board/board-page/` (modified)

`board-page.ts` replaces the `ISSUE_STATUSES`-derived `columns` computed with a `groups` signal fed
by `queryBoard`. Filter state lives in one `signal<BoardQueryRequest>`, mirrored to the URL query
string so a reload restores the view, and an `effect` refetches on change. The kanban and list
renderings are two `@if` branches over the **same** `groups` signal — `AC-BXP-006` and `AC-BXP-008`
by construction. `applyChange`'s handling of `REALTIME_ISSUE_CHANGED` becomes a refetch of the
current query rather than a local array mutation, because a filtered view cannot decide locally
whether a changed issue still matches.

### 8.13 `src/anvilboard-web/src/app/board/activity-feed/` (new)

Presentational: takes `entries` as a required input, renders type icon, actor, relative timestamp
and text. `issue-detail` owns the fetch and the `REALTIME_ACTIVITY_ADDED` subscription, prepending
without a refetch (`AC-BXP-010`).

### 8.14 `src/anvilboard-web/src/app/board/issue-detail/` (modified)

Adds the activity panel and a comments fetch on open (fixing the reload-empties-panel bug in §2.1).
Each link row gains an inline edit affordance calling `updateIssueLink`. `SUGGESTED_LINK_TYPES` is
deleted and the datalist is fed from `listLinkTypes()`.

## 9. API Design

### 9.1 REST Overview

| Method | Route | Permission | Success |
|---|---|---|---|
| `GET` | `/api/board` | `ReadBoard` | 200 + `BoardQueryResultDto` |
| `GET` | `/api/issues/{id}/activity` | `ReadBoard` | 200 + `ActivityPageDto` |
| `GET` | `/api/issues/{id}/comments` | `ReadBoard` | 200 + `CommentPageDto` |
| `PATCH` | `/api/issues/{id}/links/{linkId}` | `ReadWriteIssues` \| `ReadWriteAssignedIssues` | 200 + `IssueLinkDto` |
| `GET` | `/api/projects` | `ReadBoard` | 200 + `ProjectDto[]` |
| `GET` | `/api/labels` | `ReadBoard` | 200 + `LabelDto[]` |
| `GET` | `/api/issue-link-types` | `ReadBoard` | 200 + `LinkTypeVocabularyDto` |

No new permission is introduced. Every read reuses `ReadBoard` — a contributor who can see the
board can see why an issue is where it is, which is the point of the feed. The only mutation reuses
the same permission pair as every other issue mutation.

### 9.2 Board Response Contract

```jsonc
// GET /api/board?groupBy=ASSIGNEE&orderBy=PRIORITY&provider=GITHUB&syncCondition=STALE&limit=25
{
  "groups": [
    {
      "key": "…member-guid…",
      "displayName": "arjen",
      "issues": [
        {
          "id": "…guid…",
          "key": "COM-234",
          "title": "Webhook receiver drops duplicate deliveries",
          "workflowStateId": "…guid…",
          "type": "bug",
          "priority": "HIGH",
          "assigneeId": "…guid…",
          "projectId": null,
          "provider": "GITHUB",
          "labelIds": ["…guid…"],
          "createdAt": "2026-09-02T08:11:00+00:00",
          "updatedAt": "2026-09-11T22:40:00+00:00",
          "archivedAt": null
        }
      ]
    }
  ],
  "totalCount": 37,
  "page": 1,
  "limit": 25,
  "nextCursor": "b2Zmc2V0OjI1",
  "appliedQuery": {
    "workflowStateId": null, "assigneeId": null, "provider": "GITHUB",
    "projectId": null, "priority": null, "type": null, "labelId": null,
    "syncCondition": "STALE", "groupBy": "ASSIGNEE", "orderBy": "PRIORITY",
    "page": 1, "limit": 25, "includeArchived": false
  }
}
```

`appliedQuery` is `FR-WRK-001 AC1` made literal, and it includes the defaults the caller omitted —
which is what lets the empty-state UI name the active filters for `AC-BXP-004`. `workspaceId` is
deliberately **absent** from the echo: the caller cannot set it and does not need it confirmed.

### 9.3 Activity Response Contract

```jsonc
// GET /api/issues/{id}/activity?limit=50
{
  "entries": [
    {
      "id": "…guid…",
      "type": "IssueLinkUpdated",
      "actorId": "…guid…",
      "actorDisplayName": "arjen",
      "occurredAt": "2026-09-13T14:02:00+00:00",
      "data": { "linkId": "…guid…", "type": "blocks" },
      "text": "arjen updated an issue link"
    }
  ],
  "nextCursor": null
}
```

`type` is the `ActivityEventType` member name verbatim (`Created`, `StatusChanged`, `CommentAdded`,
`IssueLinkUpdated`, …), not a SCREAMING_SNAKE re-spelling — the serializer emits the .NET enum name
and the client matches on it directly. `data` carries the stored `DataJson` verbatim, or `null` when
the row has no payload or the payload fails to parse; `text` is the flat, already-rendered fallback —
the structured/flat pair `FR-WRK-013 AC3` asks for, without yet committing to the full template
vocabulary (§3.3). A `null` `nextCursor` means the feed is exhausted; there is no separate `hasMore`.

### 9.4 Link Update Contract

```jsonc
// PATCH /api/issues/{id}/links/{linkId}
{ "type": "BLOCKS", "description": "blocks the 0.4 release" }
// Both fields optional; an omitted field is left unchanged.

// 409 — the new type collides with an existing link between the same pair
{ "title": "RESOURCE_ALREADY_EXISTS", "status": 409,
  "detail": "A link of this type already exists between these issues." }
```

### 9.5 Agent Surface

Five operations (§8.8) whose parameters and results mirror the routes above one-for-one. The
`AgentCatalogInvariantsTests` structural suite enforces that mirroring, which is why no separate
"keep them in sync" checklist is needed.

## 10. Data & Storage Design

**No migration.** No new table, no new column, no changed column type.

The only persisted change is one appended `ActivityEventType` member, stored as an `int` by the
existing configuration. Appending at the end of the enum keeps every existing ordinal stable
(`DR-BXP-005`).

The read paths rely on indexes that already exist: `ActivityEventConfiguration` declares
`HasIndex(a => a.IssueId)` and `HasIndex(a => a.OccurredAt)`, which is precisely the
filter-then-sort the feed issues. `BoardQueryService`'s access pattern is unchanged.

Two inherited performance characteristics are worth stating rather than discovering:
`BoardQueryService` filters `LabelId` **client-side after materialization**, and its cursor is an
encoded offset rather than a keyset. Both are acceptable at Anvilboard's single-file, single-team
scale and both are recorded as follow-ups (§13.2) rather than silently carried.

## 11. Security Design

### 11.1 Authentication

Every new route sits behind the existing `WorkspaceAuthorizationMiddleware`; every new agent
operation behind `WorkspaceAuthorizationPolicy`. No route is anonymous and none introduces its own
authentication path.

### 11.2 Authorization

Reads require `ReadBoard`; the one mutation requires `ReadWriteIssues` or
`ReadWriteAssignedIssues`, identical to every other issue mutation in `IssueEndpoints`. Enforcement
happens once, in the middleware/policy — handlers never re-check. Agent effective permissions

`GET /api/issues/{id}/comments` reads under `ReadBoard` rather than `ReadWriteComments` (which
guards the existing `POST`), because `ReadWriteComments` is a *write* grant. This widens nothing in
practice: `RolePermissionMap` gives all four roles both permissions, so the two are coextensive
today — but the read is correctly classified if the map ever diverges. Agent effective permissions
remain the intersection of the credential's grants and the actor's role, so a `Contributor`-backed
automation cannot update a link it could not update through the web.

No new `Permission` member is added. Introducing one for the activity feed was considered and
rejected: activity is a rendering of board data the actor can already read, and a separate
permission would let a role see an issue but not why it moved, which is a worse product and a
larger enforcement surface for no security gain.

### 11.3 Data Protection & Isolation

`GET /api/board` accepts **no** `workspaceId`; it comes from the actor. Every other caller-supplied
id — `workflowStateId`, `assigneeId`, `projectId`, `labelId`, `issueId`, `linkId` — is resolved
through `RestWorkspaceScope` before it reaches an application service, and unknown and
out-of-workspace ids produce the byte-identical `403 WORKSPACE_ACCESS_DENIED`, so no new route
becomes an existence oracle for another workspace's data. `AC-BXP-017` puts every one of them into
the existing `CrossWorkspaceIsolationEndpointTests` sweep so this is a test, not a convention.

`ActivityQueryService` joins through `Teams.WorkspaceId` before projecting, so a valid `issueId`
from another workspace yields no rows even if scoping were somehow bypassed — defense in depth, not
the primary control.

The activity contract exposes `actorDisplayName` but no email, no credential, and no token. The
link-type vocabulary is a static list and carries no workspace data at all. `GET /api/projects` and
`GET /api/labels` return only display fields and take no caller-supplied id.

## 12. Testing Strategy

| Suite | File | Covers |
|---|---|---|
| Application unit | `src/Anvilboard.Application.Tests/Activity/ActivityQueryServiceTests.cs` (new) | `AC-BXP-009` — ordering, paging, cursor stability, workspace scoping, `Data`/`Text` pairing, batched actor resolution |
| Application unit | `Issues/IssueLinkServiceTests.cs` (extended) | `AC-BXP-012`, `AC-BXP-013` — field preservation, both-sides activity, uniqueness re-check on type change, no-op patch rejection, description-only patch skipping the check |
| Application unit | `Issues/BoardQueryServiceTests.cs` (extended) | `AC-BXP-004`, `AC-BXP-005` — the empty-but-successful contract and deterministic repeat paging, asserted at the service boundary the new route depends on |
| API integration | `src/Anvilboard.Api.Tests/Board/BoardEndpointTests.cs` (new) | `AC-BXP-001`, `AC-BXP-002`, `AC-BXP-004`, `AC-BXP-005` — every parameter binds and reaches the service; `appliedQuery` echo including defaults; bad `page`/`limit` → 400; unknown `groupBy` token → 400 |
| API integration | `src/Anvilboard.Api.Tests/Issues/IssueActivityEndpointTests.cs` (new) | `AC-BXP-009`, `AC-BXP-011` — shape, ordering, paging; comments survive a second request |
| API integration | `src/Anvilboard.Api.Tests/Issues/IssueLinkEndpointTests.cs` (new — the link routes have no API-level suite today, only `Application.Tests/Issues/IssueLinkServiceTests.cs`) | `AC-BXP-012`, `AC-BXP-013`, `AC-BXP-014` — 200/409/403, and a `linkId` belonging to a different issue |
| API integration | `src/Anvilboard.Api.Tests/Issues/TaxonomyEndpointTests.cs` (new) | `AC-BXP-015` — vocabulary shape and `enforced: false`; projects/labels scoped to the actor's workspace |
| API integration | `Authorization/CrossWorkspaceIsolationEndpointTests.cs` (extended) | `AC-BXP-003`, `AC-BXP-017` — all seven new routes join the existing sweep |
| Agent | `src/Anvilboard.Agent.Tests/BoardQueryOperationTests.cs` (new — the project is flat, mirroring `IntegrationHealthOperationTests.cs`) | `AC-BXP-016` — permission parity with the REST twins for all five operations |
| Agent structural | `AgentCatalogInvariantsTests.cs` (extended) | automatic once the four read operations are listed in `OperationsWithoutIdempotencyKey` |
| Web unit | `board/board-page/board-page.spec.ts` (extended) | `AC-BXP-006`, `AC-BXP-007`, `AC-BXP-008` — kanban and list issue the identical request; view switch preserves filter/group/order; URL round-trip restores state |
| Web unit | `board/board-filters/board-filters.spec.ts` (new) | control → `BoardQueryRequest` mapping for every dimension |
| Web unit | `board/activity-feed/activity-feed.spec.ts` (new) | `AC-BXP-010` — renders entries; prepends on `REALTIME_ACTIVITY_ADDED` without refetching |
| Web unit | `board/issue-detail/issue-detail.spec.ts` (new) | `AC-BXP-011`, `AC-BXP-015` — comments load on open; link edit posts a `PATCH`; datalist is server-fed |

The three existing Angular spec files (`app.spec.ts`, `board-page.spec.ts`,
`realtime-board-sync.service.spec.ts`) mean the web suite is thin; this plan roughly doubles it,
deliberately, because the board UI is where most of this change's risk lives.

Verification:

```powershell
dotnet test src\Anvilboard.Application.Tests\Anvilboard.Application.Tests.csproj --configuration Debug
dotnet test src\Anvilboard.Api.Tests\Anvilboard.Api.Tests.csproj --configuration Debug
dotnet test src\Anvilboard.Agent.Tests\Anvilboard.Agent.Tests.csproj --configuration Debug
dotnet test Anvilboard.slnx    # full suite; 443 passing before this change

cd src\anvilboard-web
npm test -- --watch=false      # 21 passing before this change
npm run build
```

**Result.** `dotnet test Anvilboard.slnx` — **521 passing, 0 failing** (Application 283, API 115,
Agent 65, Infrastructure 41, GitHub 12, Linear 5), up from 443. `npm test` — **44 passing** across
six spec files, up from 21. `npm run build` is clean. Two production bugs surfaced during this work
and are fixed: `ActivityQueryService` and `IssueService.ListCommentsAsync` both ordered by a
`DateTimeOffset`, which SQLite cannot translate.

## 13. Milestones & Task Breakdown

| # | Task | Files | Depends on | Done |
|---|---|---|---|:-:|
| **T1** | `ActivityEventType.IssueLinkUpdated`, appended last | `Domain/ActivityEvent.cs` | — | ✅ |
| **T2** | `RequireProjectAsync` / `RequireLabelAsync` (+ nullable overloads) on both scopes | `Api/Authorization/RestWorkspaceScope.cs`, `Agent/Authorization/AgentWorkspaceScope.cs` | — | ✅ |
| **T3** | `IActivityQueryService` / `ActivityQueryService` / DTOs; DI registration | `Application/Activity/ActivityQueryService.cs`, `Application/ServiceCollectionExtensions.cs` | — | ✅ |
| **T4** | `IssueLinkService.UpdateLinkAsync` + uniqueness re-check + both-sides activity | `Application/Issues/IssueLinkService.cs` | T1 | ✅ |
| **T5** | `IssueService.ListCommentsAsync` | `Application/Issues/IssueService.cs` | — | ✅ |
| **T6** | Link-type vocabulary constant | `Domain/IssueLink.cs` | — | ✅ |
| **T7** | `BoardEndpoints` + `BoardQueryRequest` + DTO projection + `Program.cs` mapping | `Api/Endpoints/BoardEndpoints.cs`, `Api/Program.cs` | T2 | ✅ |
| **T8** | `TaxonomyEndpoints` (projects, labels, link types) + mapping | `Api/Endpoints/TaxonomyEndpoints.cs`, `Api/Program.cs` | T6 | ✅ |
| **T9** | `IssueEndpoints`: `GET /{id}/activity`, `GET /{id}/comments`, `PATCH /{id}/links/{linkId}`; legacy `GET /api/issues` marked deprecated in OpenAPI | `Api/Endpoints/IssueEndpoints.cs` | T3, T4, T5 | ✅ |
| **T10** | Five agent operations + `OperationsWithoutIdempotencyKey` entries | `Agent/BoardAgentService.cs` | T3, T4, T6, T2 | ✅ |
| **T11** | .NET tests per §12 | see §12 | T7–T10 | ✅ |
| **T12** | Angular models | `web/src/app/core/models.ts` | T7–T9 | ✅ |
| **T13** | `BoardApiService` methods | `web/src/app/core/board-api.service.ts` | T12 | ✅ |
| **T14** | `board-filters` component | `web/src/app/board/board-filters/` | T13 | ✅ |
| **T15** | `board-page`: `queryBoard`-driven groups, kanban ⇄ list, URL-mirrored filter state | `web/src/app/board/board-page/` | T14 | ✅ |
| **T16** | `activity-feed` component | `web/src/app/board/activity-feed/` | T13 | ✅ |
| **T17** | `issue-detail`: activity panel, comment fetch, link edit, server-fed link types | `web/src/app/board/issue-detail/` | T16 | ✅ |
| **T18** | Angular specs per §12 | see §12 | T15, T17 | ✅ |
| **T19** | Docs: statuses, findings, milestones, changelog | see §13.1 | T11, T18 | ✅ |

T1–T6 are independent and can land together; T7–T10 are the adapter layer; T12–T18 are the web
slice and can proceed in parallel with T11 once T9 is merged.

**All of T1–T19 are complete.** Two deviations from the plan as written, both deliberate:

- **T8 shipped more than planned.** `TaxonomyEndpoints` carries `GET /api/projects` and
  `GET /api/labels` alongside `GET /api/issue-link-types`, because the board filter bar (T14) needs
  the same workspace-scoped vocabulary reads and a single taxonomy endpoint file is cheaper than
  three scattered ones.
- **Two production bugs were found and fixed while wiring the read paths**, neither anticipated here:
  `ActivityQueryService` and `IssueService.ListCommentsAsync` both ordered by a `DateTimeOffset`,
  which the SQLite provider cannot translate. Both now filter in the database and sort in memory
  with `Id` as a total tiebreak (§10).

### 13.1 Canonical documents to update on completion

| Document | Change | Done |
|---|---|:-:|
| [`docs/audit-report.md`](../audit-report.md) | Mark **MAJ-007**, **MAJ-008**, **MAJ-010**, **MAJ-011** RESOLVED; strike through priority action **#8** and link this plan | ✅ |
| [`docs/features/overview.md`](../features/overview.md) | Row 3 (`issue-board-service`) and row 9 (`issue-linking`): link this plan and update Status; re-stamp the header "Last verified" line | ✅ |
| [`docs/features/issue-board-service.md`](../features/issue-board-service.md) | Status row: drop "the web UI only groups by status (no filter stack)" and "the issue-detail activity feed is not rendered"; keep the threaded-comments and `Issue.Version` clauses (out of scope) | ✅ |
| [`docs/features/issue-linking.md`](../features/issue-linking.md) | Status row: drop "a link-update endpoint is missing" and the client-only-suggestion clause; re-stamp "Last verified" (it still cites the 394-test MAJ-022 change set) | ✅ |
| [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) | §16: **M3.5** and **M4.5** status text; §9 API design gains the new routes; re-stamp the §16 "Last verified" line (INFO-005) | ✅ |
| [`docs/anvilboard/test-cases.md`](../anvilboard/test-cases.md) | Add cases under §3.2 (issue/board) and §3.4 (automation contracts); add an `AC-BXP-001`–`AC-BXP-017` block to §5.3 feature-spec AC coverage; refresh the §1.2 testable-unit rows for the new services and the §7 statistics | ✅ |
| [`CHANGELOG.md`](../../CHANGELOG.md) | `## [Unreleased] → ### Added` entry referencing this plan; a `### Deprecated` entry for `GET /api/issues` | ✅ |
| [`README.md`](../../README.md) | Verified at completion: the Kanban bullet understated the UI, so it now names the filter/group-by stack, the server-side grouping, and the activity/comment panel | ✅ |

### 13.2 Deliberate follow-ups (not this plan)

| Item | Why deferred |
|---|---|
| Retire `GET /api/issues` and the legacy `IssueStatus` filter — the documented MIN-003 residual | Needs one release of deprecation notice; `DR-BXP-002` |
| Structured activity templates in full (`FR-WRK-013 AC1`–`AC2`) | Designing the template vocabulary is a spec task of its own; this plan ships `data` + `text` |
| Threaded comments (MAJ-006) | Schema change plus spec correction; independent slice |
| `Issue.Version` enforcement across all mutation paths (MAJ-009) | Priority action scope; no mutation added here depends on it |
| Workspace-scoped audit query (MAJ-018, `FR-OPS-001`) | Priority action **#9**; audit ≠ activity |
| Keyset pagination and server-side label filtering in `BoardQueryService` | Existing characteristics, not regressions; `DR-BXP-004` |
| Saved/named board views and shareable links | Product feature on top of the query surface this plan builds |
| Project and label **write** surfaces | Only reads are needed to populate filters; CRUD is its own slice |

## 14. Open Questions & Decision Records

| ID | Question / Decision | Status | Decision |
|---|---|---|---|
| `DR-BXP-001` | New `/api/board` route, or retrofit `/api/issues`? | Resolved | **New route** (§5 Option A). Retrofitting changes the response shape for every existing caller and forces an immediate decision on the legacy `status` filter; a new route closes MAJ-007 without breaking anything and speaks `workflowStateId` from birth. |
| `DR-BXP-002` | How long does the legacy `GET /api/issues` survive? | Resolved | **One release.** Marked deprecated in OpenAPI by T9 and recorded in `CHANGELOG.md`; removal is §13.2's first row. It stays this release because `issue-detail.ts` still uses it for the link-target picker. |
| `DR-BXP-003` | Does the activity feed need its own `Permission`? | Resolved | **No.** Activity is a rendering of board data the actor can already read. A separate permission would let a role see an issue but not why it moved — worse product, larger enforcement surface, no security gain. |
| `DR-BXP-004` | Fix `BoardQueryService`'s offset cursor and client-side label filter here? | Resolved | **No.** Both are pre-existing and correct-if-slow at Anvilboard's scale. Changing the service while simultaneously giving it its first caller would make a regression impossible to attribute. Recorded in §13.2. |
| `DR-BXP-005` | Where does `IssueLinkUpdated` go in `ActivityEventType`? | Resolved | **Last**, after `ArtifactRemoved`. `ActivityEventConfiguration` declares no `HasConversion`, so EF persists the underlying `int`; inserting mid-enum would silently renumber every `Artifact*` row already in every user's database. |
| `DR-BXP-006` | Should `PATCH` on a link allow changing the target issue? | Resolved | **No.** Retargeting is a different link; allowing it would make `CreatedAt` meaningless and would need a second uniqueness check against a pair the caller never named. Delete-and-recreate remains correct for that case. |
| `DR-BXP-007` | Is `GET /api/issues/{id}/comments` in scope, given the audit report never filed it? | Resolved | **Yes.** It was discovered while verifying MAJ-008 (§2.1): the comments panel currently empties on reload. It is one route over an existing service, it shares the activity panel's paging shape, and shipping an activity feed next to a comment list that silently loses data would be a worse outcome than the finding we set out to fix. Recorded here because it is an addition to the audit's stated scope. |
| `DR-BXP-008` | Do projects and labels need write endpoints for the filter UI? | Resolved | **No.** The pickers need to *list*; nothing in this plan creates a project or a label. CRUD is §13.2. |
| `DR-BXP-009` | Should an unrecognized `groupBy`/`syncCondition` token 400, given `priority` does not? | Resolved | **Yes, 400.** `priority` and `type` are free-form filter values where "matches nothing" is a sensible answer. `groupBy`, `orderBy`, `syncCondition` and `provider` are closed vocabularies where a typo can only be a caller bug, and silently falling back to a default would hide it. |
