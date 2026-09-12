# Implementation Plan: Workspace-Bound REST & Application Queries

> Feature-level technical design and execution plan for **MAJ-022**, the last open gap on
> Anvilboard's P0 #1 component and the only remaining finding that is a live cross-tenant
> data-exposure hole rather than a missing capability.
> Generated with Spec-Forge `tech-design-generation` against the canonical chain:
> [`prd.md`](../anvilboard/prd.md) → [`srs.md`](../anvilboard/srs.md) →
> [`tech-design.md`](../anvilboard/tech-design.md) →
> [`workspace-authorization.md`](../features/workspace-authorization.md).

## 1. Document Information

| Field | Value |
|---|---|
| Document | Implementation Plan — Workspace-Bound REST & Application Queries |
| Feature component | [`workspace-authorization`](../features/workspace-authorization.md) (query-scoping half) |
| Milestone | [`tech-design.md`](../anvilboard/tech-design.md) §16 **M1: Workspace & Auth Foundation** (also referenced by **M4**) |
| Audit finding | [`audit-report.md`](../audit-report.md) **MAJ-022**; residual clause of priority action **#4** |
| SRS refs | `FR-WS-001` (primary), `NFR-SEC-002` (primary), `FR-WRK-001`/`FR-WRK-003`/`FR-OPS-001` (touched) |
| Acceptance criteria | `AC-002` (existing, [`workspace-authorization.md`](../features/workspace-authorization.md)) + new `AC-301`–`AC-310` |
| Status | **Delivered** — all tasks T1–T14 complete; `dotnet test Anvilboard.slnx` 348 passing, `npm test` 21 passing. See §18 for the four deviations from this plan as written. |
| Created | 2026-09-12 |

## 2. Why this feature was selected

All three **Critical** findings are closed (`CRIT-001`/`CRIT-002`/`CRIT-003` all `RESOLVED` in
[`audit-report.md`](../audit-report.md)), and priority actions 1–3 are struck through. Selecting
"the next feature" therefore means selecting the largest verified gap among the open **Major**
findings — and among those, MAJ-022 is distinguished by kind, not just by size.

| Signal | Finding |
|---|---|
| Open GitHub issues | `gh issue list --state open` → only `#41 Dependency Dashboard` (Renovate bot). There is no human backlog on GitHub; the backlog lives in [`docs/features/overview.md`](../features/overview.md) + [`docs/audit-report.md`](../audit-report.md). |
| Newest open Major | **MAJ-022**, filed as the residual of the work that closed MAJ-001/MAJ-015 (commit `e3e03a5`, "enforce workspace authorization on CLI and MCP surfaces"). Closing the CLI/MCP half of the leak made the REST half the sole remaining one. |
| Severity kind | Every other open Major is *"a capability the spec promises is absent."* MAJ-022 is *"a shipped, reachable, authenticated code path returns another tenant's data."* It is the only open finding that is exploitable rather than merely incomplete. |
| Blocking relationship | [`workspace-authorization.md`](../features/workspace-authorization.md) lists `Blocks: workflow-engine, issue-board-service, integration-and-plugin-platform, agent-and-automation-surface, audit-and-recovery` — i.e. **every other component**. It is the one row in the feature index whose completion unblocks the rest. |
| Requirement status | `NFR-SEC-002`'s target is *"100% of defined cross-workspace read/mutation attempts are denied and produce no protected data."* Today the REST channel's measured figure is **0%** — there is not one cross-workspace REST test in the suite. |
| Milestone status | `tech-design.md` §16 **M1** is the *first* milestone and still `Partial`, with MAJ-022 named as the only remaining item. Milestones M2–M8 are all building on an unfinished foundation. |

### 2.1 Verified code evidence

Every claim below was confirmed against the working tree at commit `e3e03a5`.

| Evidence | Location |
|---|---|
| `IssueService.GetAsync` resolves by primary key with **no** workspace predicate: `db.Issues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct)` | [`IssueService.cs`](../../src/Anvilboard.Application/Issues/IssueService.cs) line 45 |
| `IssueService.ListAsync` takes `WorkspaceId? workspaceId = null` — **optional**, and omitted the filter is skipped entirely, starting from all issues host-wide | [`IssueService.cs`](../../src/Anvilboard.Application/Issues/IssueService.cs) lines 48–63 |
| `DashboardService.GetSummaryAsync` has the identical optional-`WorkspaceId?` shape | [`DashboardService.cs`](../../src/Anvilboard.Application/Dashboard/DashboardService.cs) lines 15–26 |
| REST never supplies it: `service.ListAsync(team, status, assignee, ct: ct)` — the positional `workspaceId` argument is *skipped* using a named `ct:` argument | [`IssueEndpoints.cs`](../../src/Anvilboard.Api/Endpoints/IssueEndpoints.cs) lines 22–29 |
| REST never supplies it: `service.GetSummaryAsync(teamId…, ct: ct)` | [`DashboardEndpoints.cs`](../../src/Anvilboard.Api/Endpoints/DashboardEndpoints.cs) line 13 |
| The CLI/MCP surface *does* supply it — `issues.ListAsync(await scope.RequireTeamAsync(t, ct), status, assignee, scope.WorkspaceId, ct)` — proving the divergence is REST-only | [`BoardAgentService.cs`](../../src/Anvilboard.Agent/BoardAgentService.cs) lines 69–73, 174–176 |
| `IssueService.CreateAsync` accepts any `TeamId` and never checks the team's workspace | [`IssueService.cs`](../../src/Anvilboard.Application/Issues/IssueService.cs) lines 73–87 |
| `ChangeStatusAsync` / `AssignAsync` / `AddCommentAsync` all start from `db.Issues.FirstOrDefaultAsync(i => i.Id == id, ct)` | [`IssueService.cs`](../../src/Anvilboard.Application/Issues/IssueService.cs) lines 136, 185, 202 |
| `IssueLinkService.ListLinksAsync` / `RemoveLinkAsync` filter only on `IssueId`/`IssueLinkId` | [`IssueLinkService.cs`](../../src/Anvilboard.Application/Issues/IssueLinkService.cs) lines 68, 81 |
| `IssueLinkService.CreateLinkAsync` requires both issues to share **a** workspace but never that it is the **caller's** — an `acme` actor can link two `contoso` issues together | [`IssueLinkService.cs`](../../src/Anvilboard.Application/Issues/IssueLinkService.cs) lines 32–41 |
| `ArtifactService.EnsureIssueExistsAsync` joins `Issues`→`Teams` only to prove *reachability*, explicitly not workspace membership — its own XML doc says so | [`ArtifactService.cs`](../../src/Anvilboard.Application/Artifacts/ArtifactService.cs) lines 178–196 |
| The correct REST pattern already exists and is used elsewhere: `var workspaceId = http.GetActorContext().WorkspaceId;` then `.Where(t => t.WorkspaceId == workspaceId)` | [`TeamEndpoints.cs`](../../src/Anvilboard.Api/Endpoints/TeamEndpoints.cs) lines 24–60 |
| Zero cross-workspace REST tests exist: [`WorkspaceAuthorizationEndpointTests.cs`](../../src/Anvilboard.Api.Tests/Authorization/WorkspaceAuthorizationEndpointTests.cs) has 4 tests (bootstrap ×2, credential list, credential revoke) | — |
| …but the two-tenant harness they would need **already exists**: `ApiFactory.SeedAdditionalWorkspaceAsync(slug, username, password)` | [`ApiFactory.cs`](../../src/Anvilboard.Api.Tests/Testing/ApiFactory.cs) lines 68–115 |

### 2.2 The exploit, concretely

1. Alice bootstraps workspace `acme` and authenticates. `WorkspaceAuthorizationMiddleware` attaches
   an `ActorContext { WorkspaceId = acme }` to `HttpContext.Items` and confirms she holds
   `Permission.ReadBoard`. **Authentication and permission checks both pass — correctly.**
2. Alice issues `GET /api/issues` with no `teamId` filter. `IssueEndpoints` calls
   `ListAsync(null, null, null, ct: ct)`. Not one predicate is applied. She receives **every issue
   in every workspace on the host**, including `contoso`'s.
3. Alice issues `GET /api/dashboard/summary`. Same shape: aggregate counts across all tenants.
4. Alice reads an issue id out of step 2 and issues
   `PATCH /api/issues/{contosoIssueId}/status`. She holds `ReadWriteIssues` **in `acme`**, the
   middleware approves, and `ChangeStatusAsync` resolves the issue by primary key — it transitions
   another tenant's issue, emits an activity event, and publishes a realtime notification.

The middleware is not broken. It answers *"is this actor authenticated, and does this actor's role
grant this operation?"* — and both answers are legitimately yes. Nothing in the system asks the
third question: *"is the entity this operation names inside the actor's workspace?"* That question
is exactly what `AgentWorkspaceScope` asks on the CLI/MCP side, and what nothing asks on REST.

## 3. Overview

### 3.1 Background

`WorkspaceAuthorizationMiddleware` established REST authentication and permission enforcement, and
commit `e3e03a5` brought the CLI/MCP surface to parity via `WorkspaceAuthorizationPolicy` +
`AgentWorkspaceScope`. The agent work introduced the missing concept — *identifier scoping* — but
implemented it only in `Anvilboard.Agent`. The application services it calls were left with
**optional** `WorkspaceId?` parameters so that the existing REST callers would keep compiling.

That optionality is the defect. A security predicate that defaults to "absent" fails open: every
present and future call site is correct only by vigilance, and the compiler cannot help. The audit
records this precisely:

> **Issue**: REST authorization establishes an authenticated workspace but application queries
> resolve supplied issue/team/member IDs by primary key alone, and omitted team filters start from
> all issues.
>
> **Fix**: Make workspace identity an application-service query input (or an ambient, mandatory
> application authorization context), apply it before every entity lookup and aggregate, and add
> two-workspace REST integration tests covering explicit foreign IDs and omitted filters.
>
> — [`audit-report.md`](../audit-report.md), MAJ-022

### 3.2 Goals

| # | Goal |
|---|---|
| G1 | Every REST read and mutation resolves entities within the authenticated actor's workspace only. |
| G2 | Workspace scoping is **structurally mandatory**, not conventionally applied — omitting it is a compile error, not a silent leak. |
| G3 | REST and CLI/MCP produce identical isolation behavior and identical error codes for the same probe. |
| G4 | `NFR-SEC-002` reaches its 100% target with an executable two-workspace suite covering every affected route. |
| G5 | Denials leak no existence information about other workspaces' data. |
| G6 | The one legitimate unscoped path — webhook/polling ingestion — is preserved and *named* as such, not left as an accident. |

### 3.3 Non-Goals

| # | Non-goal | Rationale |
|---|---|---|
| NG1 | Changing the authentication or permission model | `FR-WS-001` AC(1) and AC(3) are already satisfied; this plan addresses only AC(2). |
| NG2 | Multi-workspace membership for a single actor | `ActorContext` carries exactly one `WorkspaceId`. Cross-workspace membership is a product decision outside this finding. |
| NG3 | Scoping `SyncCoordinator`'s ingestion path | Polling ingestion has no authenticated workspace to scope by; see §3.4 *Explicitly excluded* and DR-4. |
| NG4 | Row-level security / EF global query filters as the mechanism | Evaluated as Solution C and rejected; see §5.3. |
| NG5 | API route versioning (`/api/v1`) | Pre-existing, cross-cutting, and orthogonal — same disposition as `backup-and-restore.md` OQ-P1. |
| NG6 | A REST board-query endpoint over `IBoardQueryService` | `IBoardQueryService` is registered but unexposed. Exposing it is `issue-board-service` work; this plan only cites it as the correct-shape precedent. |

### 3.4 Scope

**Included — application services (make workspace scoping mandatory):**

- [`IssueService`](../../src/Anvilboard.Application/Issues/IssueService.cs): `GetAsync`, `ListAsync`,
  `CreateAsync`, `ChangeStatusAsync`, `AssignAsync`, `AddCommentAsync`.
- [`DashboardService`](../../src/Anvilboard.Application/Dashboard/DashboardService.cs): `GetSummaryAsync`.
- [`IssueLinkService`](../../src/Anvilboard.Application/Issues/IssueLinkService.cs): `ListLinksAsync`,
  `RemoveLinkAsync`, and `CreateLinkAsync`. `CreateLinkAsync` is only *partially* safe today: it joins
  `Issues`→`Teams` and requires both endpoints to share **a** workspace, but never checks that it is
  the **caller's** workspace — so an actor in `acme` can still link two of `contoso`'s issues to each
  other. It needs the same treatment as the other two, not merely re-expression.
- [`IArtifactService`](../../src/Anvilboard.Application/Artifacts/IArtifactService.cs) /
  [`ArtifactService`](../../src/Anvilboard.Application/Artifacts/ArtifactService.cs):
  `AttachArtifactAsync`, `ListArtifactsAsync`, `RemoveArtifactAsync`.

**Included — REST endpoints (supply the authenticated workspace):**

- [`IssueEndpoints`](../../src/Anvilboard.Api/Endpoints/IssueEndpoints.cs) — all 9 routes.
- [`DashboardEndpoints`](../../src/Anvilboard.Api/Endpoints/DashboardEndpoints.cs) — 1 route.
- [`ArtifactEndpoints`](../../src/Anvilboard.Api/Endpoints/ArtifactEndpoints.cs) — 3 routes.

**Included — tests:** a two-workspace REST integration suite (`AC-301`–`AC-308`), plus unit
coverage for the new scope helper and for each newly-scoped service method.

MAJ-022's *Location* field names only `IssueEndpoints`/`DashboardEndpoints`/`IssueService`/
`DashboardService`, but its *Fix* field says "apply it before **every** entity lookup and
aggregate." The artifact and link paths are the same leak reached through a different route and are
therefore in scope; fixing four files and leaving five equivalent holes would let the finding be
closed while the vulnerability remains.

**Explicitly excluded:**

- `IssueService.UpsertFromExternalAsync(NormalizedIssue, ct)` — the parameterless-workspace overload
  called by [`SyncCoordinator`](../../src/Anvilboard.Application/Sync/SyncCoordinator.cs) line 51.
  Polling ingestion runs on a host-level timer with no authenticated actor; there is no workspace to
  scope by. This path already handles the resulting ambiguity deliberately and explicitly (it
  `Take(2)`s candidate teams and throws a descriptive error on 0 or ≥2 matches rather than letting
  `SingleOrDefaultAsync` produce an opaque framework exception). It is renamed, not removed — see DR-4.
- [`WebhookEndpoints`](../../src/Anvilboard.Api/Endpoints/WebhookEndpoints.cs) — `[AllowAnonymous]`
  by design (external providers hold no Anvilboard credential); it derives a *trusted* workspace from
  the payload's `TeamKey` and passes it explicitly. Already correct.
- [`WorkspaceRealtimeHub`](../../src/Anvilboard.Api/Realtime/WorkspaceRealtimeHub.cs) — already derives
  group membership exclusively from the authenticated actor's own workspace.
- [`TeamEndpoints`](../../src/Anvilboard.Api/Endpoints/TeamEndpoints.cs),
  [`BackupEndpoints`](../../src/Anvilboard.Api/Endpoints/BackupEndpoints.cs),
  [`AuthEndpoints`](../../src/Anvilboard.Api/Endpoints/AuthEndpoints.cs) — already call
  `GetActorContext()` and filter on `WorkspaceId`. They are the reference pattern, not the problem.

### 3.5 User Scenarios

| ID | As a… | I want… | So that… |
|---|---|---|---|
| US-1 | tenant of workspace `acme` | `GET /api/issues` with no filters to return only `acme`'s issues | another tenant's titles, descriptions, and keys are never rendered in my board |
| US-2 | tenant of workspace `acme` | `GET /api/dashboard/summary` to aggregate only `acme`'s issues | my counts are correct and disclose nothing about host neighbours |
| US-3 | attacker authenticated in `acme` | a `GET /api/issues/{contosoIssueId}` probe to be denied | I cannot enumerate or read foreign issues by guessing or replaying ids |
| US-4 | attacker authenticated in `acme` | `PATCH /api/issues/{contosoIssueId}/status` to be denied | I cannot transition, reassign, or comment on another tenant's work |
| US-5 | attacker authenticated in `acme` | `POST /api/issues` with `teamId` from `contoso` to be denied | I cannot inject issues into a foreign board, or burn its `NextIssueNumber` |
| US-6 | attacker authenticated in `acme` | artifact and link routes on a foreign issue to be denied | attachments and relationships are not a side channel around the issue routes |
| US-7 | attacker probing for reconnaissance | an unknown id and a foreign id to be indistinguishable | denial responses are not an existence oracle for other workspaces |
| US-8 | maintainer | omitting the workspace argument to fail the build | the next endpoint added cannot reintroduce this class of bug |
| US-9 | operator running polling sync | ingestion to keep working unchanged | closing the REST leak does not break GitHub/Linear sync |

### 3.6 Acceptance Criteria

Extends `AC-002` ("a valid actor cannot read or mutate a workspace for which it lacks permission")
from [`workspace-authorization.md`](../features/workspace-authorization.md) with route-level
criteria. `AC-001`–`AC-012`, `AC-101`–`AC-108` and `AC-201`–`AC-212` are taken elsewhere in `docs/`;
this plan uses the free `AC-3xx` block.

| ID | Criterion | Verified by |
|---|---|---|
| AC-301 | `GET /api/issues` with **no** filters returns only issues whose team belongs to the authenticated workspace. | Integration (two workspaces, issues seeded in both) |
| AC-302 | `GET /api/dashboard/summary` with **no** `teamId` aggregates only the authenticated workspace's issues; every count, group, and bucket excludes foreign rows. | Integration |
| AC-303 | `GET /api/issues?teamId={foreignTeamId}` is denied with `WORKSPACE_ACCESS_DENIED` (403) and returns no issue data. | Integration |
| AC-304 | `GET /api/issues/{foreignIssueId}` is denied with `WORKSPACE_ACCESS_DENIED` (403) and returns no issue data. | Integration |
| AC-305 | Each of `PATCH /{id}/status`, `PATCH /{id}/assignee`, `POST /{id}/comments` against a foreign issue id is denied, and the target row, its `Version`, and its activity trail are provably unchanged. | Integration |
| AC-306 | `POST /api/issues` with a foreign `teamId` is denied, no issue row is created, and the foreign team's `NextIssueNumber` is unchanged. | Integration |
| AC-307 | Each of `GET/POST /{id}/artifacts`, `DELETE /{id}/artifacts/{artifactId}`, `GET/POST /{id}/links`, `DELETE /{id}/links/{linkId}` against a foreign issue id is denied with no data returned and no state changed. | Integration |
| AC-308 | A **nonexistent** id and a **foreign** id produce byte-identical responses (same status, same error code, same absence of `data`) on every ID-addressed route. | Integration |
| AC-309 | Every affected application-service method requires `WorkspaceId` as a non-optional parameter; omitting it does not compile. | Compilation + code review |
| AC-310 | `SyncCoordinator` ingestion continues to pass its existing suite unchanged. | Existing tests, unmodified |

### 3.7 Success Metrics

| Metric | Baseline | Target |
|---|---|---|
| `NFR-SEC-002` cross-workspace REST cases passing | 0 defined, 0 passing | 100% of `AC-301`–`AC-308` passing |
| Application-service methods reachable from REST with an optional/absent workspace predicate | 12 | 0 |
| Compile-time-enforced scoping on issue/dashboard/artifact/link reads | none | all |
| `dotnet test Anvilboard.slnx` | 320 passing | 320 + new cases, 0 failing |
| Divergence between REST and CLI/MCP isolation behavior | REST unscoped, agent scoped | none — same predicate, same error code |

## 4. System Context

```
                 ┌──────────────────────────── Anvilboard.Api ────────────────────────────┐
 Browser / REST  │  CorrelationIdMiddleware                                               │
 ───────────────▶│           ▼                                                            │
                 │  WorkspaceAuthorizationMiddleware  ── authN + permission ──▶ ActorContext
                 │           ▼                              (HttpContext.Items)           │
                 │  DatabaseOperationMiddleware                                           │
                 │           ▼                                                            │
                 │  IssueEndpoints / DashboardEndpoints / ArtifactEndpoints               │
                 │           │                                                            │
                 │           │  ★ TODAY: raw Guids, no workspace ───────────┐             │
                 │           │  ★ AFTER: RestWorkspaceScope.Require*Async() │             │
                 └───────────┼──────────────────────────────────────────────┼─────────────┘
                             ▼                                              ▼
                 ┌──────────────────── Anvilboard.Application ─────────────────────────────┐
                 │  IssueService · DashboardService · IssueLinkService · ArtifactService   │
                 │  ★ AFTER: WorkspaceId is a required parameter on every method below     │
                 └────────────────────────────────┬───────────────────────────────────────┘
                             ▲                    ▼
                 ┌───────────┴──────────┐   AnvilboardDbContext (EF Core / SQLite)
 CLI / MCP ─────▶│  Anvilboard.Agent    │   Issues ──TeamId──▶ Teams ──WorkspaceId──▶ Workspaces
                 │  AgentWorkspaceScope │
                 │  (already correct)   │   ← the join that defines workspace membership
                 └──────────────────────┘
```

Two facts about the data model drive the whole design:

1. **`Issue` carries no `WorkspaceId`.** Workspace membership is reached transitively:
   `Issue.TeamId → Team.WorkspaceId`. Every scoping predicate is therefore a join, not a column
   comparison — which is precisely why it was easy to omit.
2. **`ActorContext` already carries exactly the value needed.** `ActorContext.WorkspaceId` is
   populated by the middleware for every authenticated REST request. The data is present at the
   endpoint; it is simply never passed down.

## 5. Solution Design

### 5.1 Solution A — Required `WorkspaceId` parameter + `RestWorkspaceScope` helper *(recommended)*

Two coordinated changes:

**A1 — Application layer: make the predicate mandatory.** Promote `WorkspaceId` from an optional
trailing parameter to a **required leading** parameter on every affected method, and apply it inside
the query before any other filter. This mirrors the shape the codebase already treats as correct:

```csharp
public sealed record BoardQuery(
    WorkspaceId WorkspaceId,          // ← first, required, non-nullable
    WorkflowStateId? WorkflowStateId = null,
    ...);
```

— [`IBoardQueryService.cs`](../../src/Anvilboard.Application/Issues/IBoardQueryService.cs) lines 10–11

Placing it *first* is deliberate: a trailing optional parameter can be skipped with a named argument
(`ct: ct`), which is exactly the mechanism by which the current leak is written. A leading required
parameter cannot be skipped by any syntax.

**A2 — REST layer: a `RestWorkspaceScope` mirroring `AgentWorkspaceScope`.** A scoped service that
resolves the ambient `ActorContext` and validates caller-supplied ids against it:

```csharp
public sealed class RestWorkspaceScope(AnvilboardDbContext db, IHttpContextAccessor http)
{
    public WorkspaceId WorkspaceId { get; }
    public Task<TeamId>    RequireTeamAsync(Guid teamId, CancellationToken ct = default);
    public Task<IssueId>   RequireIssueAsync(Guid issueId, CancellationToken ct = default);
    public Task<MemberId?> RequireMemberAsync(Guid? memberId, CancellationToken ct = default);
}
```

The agent-side original is 90 lines and already carries the anti-oracle rationale in its class doc.
Mirroring it gives REST and CLI/MCP the *same* predicate and the *same* `WORKSPACE_ACCESS_DENIED`
code, which is what makes G3 checkable rather than aspirational.

- **Pros:** compiler-enforced (G2) — the only option where a future endpoint physically cannot omit
  scoping. Explicit at every call site, so a reviewer sees the predicate without knowing about
  ambient state. Matches two existing in-repo precedents (`BoardQuery`, `AgentWorkspaceScope`).
  Trivially unit-testable — pass two different `WorkspaceId`s, assert two different results, no host
  or `HttpContext` needed. Keeps application services free of host-specific concepts.
- **Cons:** widest diff — every call site changes, including agent call sites that are already
  correct (mechanical: delete `scope.WorkspaceId` from the tail, insert it at the head). Requires a
  named escape hatch for ingestion.

### 5.2 Solution B — Ambient `IWorkspaceContext` resolved from DI

Register a scoped `IWorkspaceContext` that the middleware populates, inject it into each application
service's constructor, and have services read `context.WorkspaceId` internally. Method signatures
are unchanged. Precedents exist:
[`CorrelationContext`](../../src/Anvilboard.Application/Automation/CorrelationContext.cs) (scoped,
resolved from the request header in `Program.cs` line 41) and
[`AgentActorAccessor`](../../src/Anvilboard.Agent/Authorization/AgentActorAccessor.cs) (throws loudly
if the policy never ran, rather than returning null).

- **Pros:** smallest diff at call sites; impossible to *forget* to pass because nothing is passed.
- **Cons:** fails at **runtime**, not compile time — `SyncCoordinator` and hooks run with no ambient
  workspace, so every such path becomes a potential production throw discovered by a customer rather
  than by the build. Application services acquire a hidden dependency on host request state, which
  makes every existing unit test that constructs a service directly need a fake context. And the
  ingestion path *legitimately* has no workspace, so B needs a suppression mechanism anyway —
  reintroducing an opt-out, which is the exact failure mode of today's optional parameter, only less
  visible.

### 5.3 Solution C — EF Core global query filters

Attach `HasQueryFilter` to `Issue`, `Artifact`, `IssueLink`, etc. in
[`AnvilboardDbContext`](../../src/Anvilboard.Infrastructure/Persistence/AnvilboardDbContext.cs), keyed
off an ambient workspace.

- **Pros:** one place; no call sites change; applies to code not yet written.
- **Cons:** **`Issue` has no `WorkspaceId` column** — every filter is a correlated subquery through
  `Teams`, on every query, for every entity. Global filters are silently bypassed by
  `IgnoreQueryFilters()` and do not apply to `Find()` or to entities already tracked in the change
  tracker, so the guarantee is not actually total. `SyncCoordinator`, `BackupService`,
  `RestoreCoordinator`, and the migration path all need host-wide access and would each need
  `IgnoreQueryFilters()` — again an opt-out, and one that is easy to paste. Debuggability suffers
  badly: the predicate is invisible at the call site, so a developer reading the endpoint has no way
  to see why a row is missing. Finally, filters constrain *reads*; `SaveChanges` on a tracked foreign
  entity is unaffected, so US-4's mutation probe is not closed by C at all.

### 5.4 Comparison Matrix

| Criterion | A: Required parameter + scope helper | B: Ambient context | C: Global query filters |
|---|---|---|---|
| Omission caught at compile time | ✅ Yes | ❌ No (runtime) | ❌ No |
| Closes foreign-id **mutations** | ✅ Yes | ✅ Yes | ❌ No |
| Visible at call site | ✅ Yes | ❌ No | ❌ No |
| Existing in-repo precedent | ✅ `BoardQuery`, `AgentWorkspaceScope` | ⚠️ `CorrelationContext` (non-security) | ❌ None |
| Unit-testable without a host | ✅ Yes | ❌ Needs a fake context everywhere | ⚠️ Needs a configured context |
| Bypass surface | Named `*Unscoped` overload (1, documented) | Suppression flag | `IgnoreQueryFilters()`, `Find()`, tracked entities |
| Handles workspace-less ingestion | ✅ Explicit overload | ⚠️ Needs suppression | ⚠️ Needs suppression |
| REST/agent behavioral parity | ✅ Same helper shape, same error code | ⚠️ Two accessors to keep in sync | ⚠️ Agent bypasses DbContext filters in places |
| Query cost | Same join as today's agent path | Same | Correlated subquery on every entity query |
| Diff size | Large (mechanical) | Small | Small |
| Review burden per future endpoint | None — compiler enforces | Must remember context ran | Must remember not to bypass |

### 5.5 Decision & Rationale

**Adopt Solution A.**

The finding is a *fail-open security predicate*. The defining property of a good fix for that class
of bug is that the failure mode becomes impossible to express, not merely unlikely — and A is the
only option where the compiler rejects the omission. B and C both narrow the window while leaving an
opt-out, which is a description of the present state.

A also has the strongest evidentiary support: the codebase already contains *two* implementations of
exactly this shape (`BoardQuery`'s required leading `WorkspaceId`, and `AgentWorkspaceScope`'s
identifier validation), both written by the team, both currently correct. Adopting A means MAJ-022
is closed by making the leaking half look like the working half — not by introducing a third pattern.

The large diff is the acceptable cost: it is entirely mechanical, fully covered by the compiler, and
it is precisely the diff that makes every leaking call site *visible in review*. A small diff would
be a warning sign here, since 12 call sites are known to be wrong.

## 6. Architecture Design

Layering is unchanged. One new type is added to `Anvilboard.Api`, and one new internal helper to
`Anvilboard.Application`.

```
Anvilboard.Api
 └─ Authorization/
     ├─ WorkspaceAuthorizationMiddleware.cs   (unchanged — still the authN/permission gate)
     ├─ HttpContextActorContextExtensions     (unchanged — GetActorContext())
     └─ RestWorkspaceScope.cs                 ★ NEW — identifier scoping, mirrors AgentWorkspaceScope
 └─ Endpoints/
     ├─ IssueEndpoints.cs                     ◆ MODIFIED — 9 routes resolve ids through the scope
     ├─ DashboardEndpoints.cs                 ◆ MODIFIED — 1 route
     └─ ArtifactEndpoints.cs                  ◆ MODIFIED — 3 routes

Anvilboard.Application
 └─ Issues/
     ├─ IssueService.cs                       ◆ MODIFIED — WorkspaceId required on 6 methods
     ├─ IssueLinkService.cs                   ◆ MODIFIED — WorkspaceId required on 3 methods
     └─ WorkspaceScopedQueries.cs             ★ NEW — the single `IQueryable<Issue>` scoping extension
 └─ Dashboard/DashboardService.cs             ◆ MODIFIED — WorkspaceId required
 └─ Artifacts/{IArtifactService,ArtifactService}.cs  ◆ MODIFIED — WorkspaceId required on 3 methods

Anvilboard.Agent
 └─ BoardAgentService.cs                      ◆ MODIFIED — mechanical argument reordering only
```

**One predicate, one place.** Every scoped query routes through a single extension so the join is
written exactly once:

```csharp
internal static class WorkspaceScopedQueries
{
    /// <summary>
    /// Constrains an issue query to one workspace. Issues carry no WorkspaceId of their own, so
    /// membership is reached through the owning team — this join is the *definition* of an issue
    /// belonging to a workspace, and every scoped read in the application layer goes through here
    /// so the rule exists in exactly one place.
    /// </summary>
    public static IQueryable<Issue> InWorkspace(
        this IQueryable<Issue> issues, AnvilboardDbContext db, WorkspaceId workspaceId) =>
        issues.Where(issue => db.Teams.Any(
            team => team.Id == issue.TeamId && team.WorkspaceId == workspaceId));
}
```

This is the identical predicate `ListAsync` already applies when a workspace *is* supplied, so the
generated SQL and its cost profile are unchanged from the agent path in production today.

## 7. Technology Stack & Conventions

### 7.1 Stack

No new dependencies. .NET 10, EF Core over SQLite, ASP.NET Core Minimal APIs, xUnit.

### 7.2 Parameter placement convention

`WorkspaceId` is the **first** parameter on every scoped application method. Rationale is
enforcement, not aesthetics: a trailing optional parameter is skippable via named arguments — the
exact mechanism (`ct: ct`) that produced this finding.

### 7.3 Scoping is applied before any other predicate

In every rewritten query the workspace join is the first `Where`, ahead of team/status/assignee
filters. This keeps the safety property independent of which optional filters a caller supplies, and
makes the "omitted filter starts from all issues" failure structurally unreachable.

### 7.4 Preserve the materialize-then-sort shape

[`ListAsync`](../../src/Anvilboard.Application/Issues/IssueService.cs) and
[`GetSummaryAsync`](../../src/Anvilboard.Application/Dashboard/DashboardService.cs) both sort and
aggregate **client-side after `ToListAsync`**, because SQLite's EF provider cannot translate
`ORDER BY`/comparisons over `DateTimeOffset`. Both carry comments saying so. The rewrite adds a
predicate to the server-side portion and must not move sorting or aggregation into the query.

### 7.5 Naming

`RestWorkspaceScope` deliberately parallels `AgentWorkspaceScope`, with identical method names
(`RequireTeamAsync`, `RequireIssueAsync`, `RequireMemberAsync`) and the identical error code. A
future reader comparing the two channels should see one pattern instantiated twice, not two designs.

### 7.6 Error catalog

No new error codes. Both codes used already exist in
[`ErrorCodeCatalog`](../../src/Anvilboard.Application/Automation/ErrorCodeCatalog.cs):

| Code | HTTP | Used when |
|---|---|---|
| `WORKSPACE_ACCESS_DENIED` | 403 | A caller-supplied id is not resolvable inside the authenticated workspace — whether foreign **or** nonexistent. |
| `REFERENCED_ENTITY_NOT_FOUND` | 404 | A dependent entity is missing *inside* the already-authorized workspace (e.g. a workflow state, or a link id not attached to an in-workspace issue). |

Note that [`ErrorCatalogTranslator`](../../src/Anvilboard.Application/Automation/ErrorCatalogTranslator.cs)
is **not currently referenced anywhere under `src/Anvilboard.Api`** — REST endpoints hand-roll
`Results.Problem(...)` switches. This plan does not refactor that; `RestWorkspaceScope` produces its
denial directly as an `IResult`, keeping the change surgical. Wiring `ErrorCatalogTranslator` into
REST is recorded as OQ-3.

### 7.7 403-vs-404: reconciling `FR-WS-001` AF-2 with the anti-oracle rule

There is a genuine tension between two in-repo rules, and the plan resolves it explicitly rather
than leaving it to the implementer.

> **AF-2** Valid credential, missing workspace permission: the system returns
> `WORKSPACE_ACCESS_DENIED` with HTTP 403 and a correlation ID; it does not return workspace data.
> **A 404 is reserved for a missing entity inside an already-authorized workspace.**
> — [`srs.md`](../anvilboard/srs.md), `FR-WS-001`

> Unknown and out-of-workspace identifiers deliberately produce the same `WORKSPACE_ACCESS_DENIED`
> error. Distinguishing them would turn every operation into an existence oracle for other
> workspaces' data.
> — [`AgentWorkspaceScope`](../../src/Anvilboard.Agent/Authorization/AgentWorkspaceScope.cs) class doc

Read naively, AF-2 implies a nonexistent `IssueId` should yield 404 while a foreign one yields 403 —
and that difference is itself the leak: an attacker distinguishes "exists in another workspace" from
"does not exist" by reading the status code, enumerating the host's entire id space.

**Decision (DR-1):** the two rules are consistent once "inside an already-authorized workspace" is
read as the operative clause. AF-2's 404 applies to entities *within* the authorized workspace; an id
that does not resolve within it — for whatever reason — is not such an entity. Therefore:

- **ID-addressed routes** (`/{id}`, `/{id}/status`, `/{id}/artifacts`, `?teamId=`, `teamId` in a
  create body): a supplied id that does not resolve inside the authenticated workspace →
  `WORKSPACE_ACCESS_DENIED` / **403**, identically for foreign and nonexistent ids. This satisfies
  AF-2's primary sentence, satisfies the anti-oracle rule, and matches the agent surface exactly.
- **Dependent lookups after scoping succeeds** (the workflow state for a transition; a link id not
  attached to an in-workspace issue): `REFERENCED_ENTITY_NOT_FOUND` / **404**, per AF-2's second
  sentence. These are genuinely "a missing entity inside an already-authorized workspace."

This is a behavior change for `GET /api/issues/{id}`, which today returns `Results.NotFound()` for an
unknown id. Under this plan it returns 403. That is the intended outcome — the 404 was the oracle —
and `AC-308` pins it. It is a REST-visible change and is called out in §14.

The [`workspace-authorization.md`](../features/workspace-authorization.md) AF-2 row gains a
clarifying sentence on completion (see §16.1) so the SRS and the code stop appearing to disagree.

## 8. Detailed Design

### 8.1 Component Overview

| Component | Responsibility | Owns |
|---|---|---|
| `WorkspaceAuthorizationMiddleware` | *Who* is calling and *what* may they do | Unchanged |
| `RestWorkspaceScope` ★ | *Which entities* may this caller name | Id → scoped-id validation, denial |
| `WorkspaceScopedQueries` ★ | The `Issue → Team → Workspace` predicate | One join, one place |
| Application services ◆ | Business rules, now over a pre-scoped query | Mandatory `WorkspaceId` input |

### 8.2 Contracts — before and after

**`IssueService`**

```csharp
// BEFORE                                              // AFTER
GetAsync(IssueId, ct)                                  GetAsync(WorkspaceId, IssueId, ct)
ListAsync(TeamId?, IssueStatus?, MemberId?,            ListAsync(WorkspaceId, TeamId?, IssueStatus?,
          WorkspaceId? = null, ct)                               MemberId?, ct)
CreateAsync(TeamId, string, …, ct)                     CreateAsync(WorkspaceId, TeamId, string, …, ct)
ChangeStatusAsync(IssueId, IssueStatus, MemberId?, ct) ChangeStatusAsync(WorkspaceId, IssueId, IssueStatus, MemberId?, ct)
AssignAsync(IssueId, MemberId?, MemberId?, ct)         AssignAsync(WorkspaceId, IssueId, MemberId?, MemberId?, ct)
AddCommentAsync(IssueId, string, MemberId?, ct)        AddCommentAsync(WorkspaceId, IssueId, string, MemberId?, ct)

UpsertFromExternalAsync(NormalizedIssue, ct)           UpsertFromExternalUnscopedAsync(NormalizedIssue, ct)  // renamed, see DR-4
UpsertFromExternalAsync(NormalizedIssue,               UpsertFromExternalAsync(WorkspaceId, NormalizedIssue, ct)
                        WorkspaceId?, ct)
```

**`DashboardService`**

```csharp
// BEFORE                                              // AFTER
GetSummaryAsync(TeamId?, WorkspaceId? = null, ct)      GetSummaryAsync(WorkspaceId, TeamId?, ct)
```

**`IssueLinkService`**

```csharp
// BEFORE                                              // AFTER
CreateLinkAsync(IssueId, IssueId, …, ct)               CreateLinkAsync(WorkspaceId, IssueId, IssueId, …, ct)
ListLinksAsync(IssueId, ct)                            ListLinksAsync(WorkspaceId, IssueId, ct)
RemoveLinkAsync(IssueId, IssueLinkId, MemberId?, ct)   RemoveLinkAsync(WorkspaceId, IssueId, IssueLinkId, MemberId?, ct)
```

**`IArtifactService`**

```csharp
// BEFORE                                              // AFTER
AttachArtifactAsync(IssueId, …, ct)                    AttachArtifactAsync(WorkspaceId, IssueId, …, ct)
ListArtifactsAsync(IssueId, ct)                        ListArtifactsAsync(WorkspaceId, IssueId, ct)
RemoveArtifactAsync(IssueId, ArtifactId, …, ct)        RemoveArtifactAsync(WorkspaceId, IssueId, ArtifactId, …, ct)
RefreshArtifactAsync(IssueId, …, ct)                   RefreshArtifactAsync(WorkspaceId, IssueId, …, ct)
```

`RefreshArtifactAsync` is included for signature uniformity even though it has no public write path
today (it is reachable only from a plugin's correlation logic) — leaving one method unscoped would
undermine the "mandatory" property the whole design rests on.

### 8.3 Core workflow — scoped read (`GET /api/issues/{id}`)

```
1. CorrelationIdMiddleware            → CorrelationContext
2. WorkspaceAuthorizationMiddleware   → authenticate; check Permission.ReadBoard
                                      → HttpContext.Items[ActorContext] = { MemberId, WorkspaceId=acme, Role }
3. IssueEndpoints  GET /{id:guid}
     var scope = http.RequestServices.GetRequiredService<RestWorkspaceScope>();
     var issueId = await scope.RequireIssueAsync(id, ct);      ← ★ NEW
         │  SELECT 1 FROM Issues i JOIN Teams t ON t.Id = i.TeamId
         │  WHERE i.Id = @id AND t.WorkspaceId = @acme
         ├─ no row  → 403 WORKSPACE_ACCESS_DENIED  (foreign OR nonexistent — identical, AC-308)
         └─ row     → IssueId
     var issue = await service.GetAsync(scope.WorkspaceId, issueId, ct);   ← scoped again, defence in depth
4. 200 OK
```

The double check is intentional. `RequireIssueAsync` gives the correct *status code* at the boundary;
the service-level predicate means the service is safe even when called from somewhere that forgot the
boundary check. Only the second is compiler-enforced, and only the first produces a good error.

### 8.4 Core workflow — scoped mutation (`POST /api/issues`)

```
1–2. as above, with Permission.ReadWriteIssues
3. IssueEndpoints  POST /
     var teamId = await scope.RequireTeamAsync(request.TeamId, ct);       ← ★ NEW, closes US-5
         └─ team not in acme (or unknown) → 403 WORKSPACE_ACCESS_DENIED
                                            ↑ nothing written; NextIssueNumber untouched (AC-306)
     var assignee = await scope.RequireMemberAsync(request.AssigneeId, ct); ← ★ NEW
     var issue = await service.CreateAsync(scope.WorkspaceId, teamId, …, ct);
         └─ CreateAsync re-verifies team.WorkspaceId == workspaceId before minting the key
4. 201 Created
```

Ordering matters: the team is validated **before** `team.NextIssueNumber++`, so a denied request
cannot advance a foreign team's issue-number sequence — a subtle side channel (and a data-corruption
vector) that a post-hoc check would leave open.

### 8.5 Aggregate read (`GET /api/dashboard/summary`)

`GetSummaryAsync` applies `InWorkspace(...)` to the base query before materializing. Since every
aggregate (`byStatus`, `bySource`, throughput, cycle time) is computed in memory from that one
materialized list, scoping the query scopes all of them consistently — no aggregate can be missed
individually, which is what makes `AC-302` verifiable as a single assertion over the whole response.

## 9. API Design

### 9.1 Overview

**No route, verb, request body, or success-response shape changes.** The entire change is which rows
each route can reach and which status code a foreign/unknown id produces.

### 9.2 Affected routes

| Route | Today | After |
|---|---|---|
| `GET /api/issues` | all issues host-wide when filters omitted | authenticated workspace only |
| `GET /api/issues?teamId={foreign}` | returns foreign team's issues | 403 `WORKSPACE_ACCESS_DENIED` |
| `GET /api/issues/{id}` | any issue by PK; 404 if absent | in-workspace only; **403** if foreign *or* absent |
| `POST /api/issues` | creates in any team | 403 unless the team is in-workspace |
| `PATCH /api/issues/{id}/status` | transitions any issue | 403 unless in-workspace |
| `PATCH /api/issues/{id}/assignee` | assigns on any issue; any `MemberId` | 403 unless issue **and** member are in-workspace |
| `POST /api/issues/{id}/comments` | comments on any issue | 403 unless in-workspace |
| `GET /api/issues/{id}/links` | lists links of any issue | 403 unless in-workspace |
| `POST /api/issues/{id}/links` | already rejects cross-workspace targets | unchanged behavior, expressed through the scope |
| `DELETE /api/issues/{id}/links/{linkId}` | removes links of any issue | 403 unless in-workspace |
| `GET /api/issues/{id}/artifacts` | lists artifacts of any issue | 403 unless in-workspace |
| `POST /api/issues/{id}/artifacts` | attaches to any issue | 403 unless in-workspace |
| `DELETE /api/issues/{id}/artifacts/{artifactId}` | removes from any issue | 403 unless in-workspace |
| `GET /api/dashboard/summary` | aggregates host-wide | authenticated workspace only |

### 9.3 Denial response

Identical to the shape `WorkspaceAuthorizationMiddleware.WriteDenialAsync` already emits — title is
the error code, no `data` member, correlation id on the response:

```
HTTP/1.1 403 Forbidden
X-Correlation-Id: 0f2c…
Content-Type: application/problem+json

{ "title": "WORKSPACE_ACCESS_DENIED", "status": 403 }
```

No detail string that could differ between the foreign and nonexistent cases — that is what makes
`AC-308`'s byte-identical requirement achievable.

### 9.4 Agent surface

Behaviorally unchanged. `BoardAgentService` already produces exactly these denials via
`AgentWorkspaceScope`; only argument *position* changes at its call sites. `Anvilboard.Agent.Tests`
should pass unmodified — and if any test needs changing, that is a signal the refactor altered agent
behavior and must be re-examined.

### 9.5 Web client

[`board-api.service.ts`](../../src/anvilboard-web/src/app/core/board-api.service.ts) calls
`/api/teams`, `/api/issues`, `/api/issues/:id`, and `/api/issues/:id/{status,assignee,comments,links}`.
It sends **no** workspace parameter and needs none — the workspace comes from the session cookie. No
client change is required. A single-workspace deployment (the default) sees no behavioral difference
at all.

## 10. Data & Storage

**No schema change. No migration.** The `Issue → Team → Workspace` relationship already exists and is
already indexed by the `Teams` primary key.

Query shape changes from:

```sql
SELECT * FROM Issues WHERE Id = @id;
```

to:

```sql
SELECT i.* FROM Issues i
WHERE i.Id = @id
  AND EXISTS (SELECT 1 FROM Teams t WHERE t.Id = i.TeamId AND t.WorkspaceId = @workspaceId);
```

This is the same SQL the agent surface issues in production today. `Teams` is small (one row per
team) and the join is on its primary key, so the added cost is negligible at the single-host scale
this project targets.

## 11. Security Design

### 11.1 Authentication

Unchanged. `WorkspaceAuthorizationMiddleware` remains the sole authentication point.

### 11.2 Authorization

This plan adds the third and final check to a chain that currently has two:

| Question | Answered by | Status |
|---|---|---|
| Is the caller authenticated? | `WorkspaceAuthorizationMiddleware` | ✅ Implemented |
| Does the caller's role grant this operation? | `RequiresPermissionAttribute` + middleware | ✅ Implemented |
| **Is the named entity inside the caller's workspace?** | `RestWorkspaceScope` + service predicates | ★ **This plan** |

### 11.3 Data protection

- Denials return no `data` member, matching `AC-002`/`AC-103`.
- Foreign and nonexistent ids are indistinguishable (§7.7, `AC-308`) — no existence oracle.
- Aggregates are scoped before materialization, so no foreign row is ever loaded into the process,
  let alone counted. Preventing the read is stronger than filtering the response.

### 11.4 Audit logging

Denials are authorization failures and follow the existing middleware convention; this plan adds no
new audit-event type. Note the *positive* audit benefit: today a cross-workspace mutation writes an
`ActivityEvent` against a foreign issue attributed to a foreign-workspace actor, corrupting that
tenant's audit trail. After this change the mutation never occurs, so the trail cannot be poisoned.

### 11.5 Residual risk

`RestWorkspaceScope` is a boundary check; the service-level required parameter is the actual
guarantee. A future endpoint that forgets the scope helper gets a poor error message (an exception
rather than a clean 403) but **cannot** leak data, because it still cannot call the service without a
`WorkspaceId` — and the only `WorkspaceId` reachable at an endpoint is the authenticated one. That
asymmetry is the point of choosing Solution A.

## 12. Performance

| Aspect | Impact |
|---|---|
| Query cost | One `EXISTS` subquery against `Teams` on its PK, per issue query. Already paid on the agent path. |
| Row volume | **Reduced** — `GET /api/issues` and the dashboard stop materializing every host row. On a multi-tenant host this is a straight improvement. |
| `NFR-PERF-001` / `NFR-PERF-002` | Unaffected; no additional round trips. `RequireIssueAsync` adds one `AnyAsync` per ID-addressed request, which is one indexed lookup. |
| Sorting/aggregation | Must remain client-side post-`ToListAsync` (§7.4). |

## 13. Observability

- Denials carry the request's correlation id, already attached by `CorrelationIdMiddleware`.
- `RestWorkspaceScope` logs denials at `Warning` with the correlation id, the actor's workspace, and
  the **kind** of entity probed — never the probed id's resolved workspace, which would move the
  oracle from the response into the log.
- A burst of `WORKSPACE_ACCESS_DENIED` from one actor is a meaningful enumeration signal; logging the
  entity kind makes that signal actionable without disclosing foreign data.

## 14. Deployment & Rollback

Ordinary code change: no migration, no config, no data backfill. Ships in one deployable unit.

**Behavioral changes visible to existing clients:**

1. `GET /api/issues/{unknownId}` now returns **403** instead of 404 (§7.7, DR-1). Deliberate.
2. On a multi-workspace host, list/dashboard responses shrink to the caller's workspace. That is the
   fix, not a regression.
3. Single-workspace deployments — the default and the documented topology in
   [`README.md`](../../README.md) — observe change (1) only.

**Rollback:** revert the commit. No state is migrated, so rollback is complete and immediate.

## 15. Testing Strategy

`NFR-SEC-002`'s target ("100% of defined cross-workspace read/mutation attempts are denied and
produce no protected data") is only meaningful once the attempts are *defined*. The REST suite
defines them.

### 15.1 Integration — the two-workspace suite

New file `src/Anvilboard.Api.Tests/Authorization/CrossWorkspaceIsolationEndpointTests.cs`, built on
the harness that already exists:

```csharp
await using var factory = new ApiFactory();
var client = factory.CreateClient();
var acmeCookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client, "acme", "alice");
var contoso   = await factory.SeedAdditionalWorkspaceAsync("contoso", "bob");   // bootstrap refuses twice
// seed a team + issue + artifact + link inside contoso directly via the DbContext,
// then probe every route as alice.
```

| Test | AC | Asserts |
|---|---|---|
| `ListIssues_WithoutFilters_ReturnsOnlyAuthenticatedWorkspace` | AC-301 | response contains acme's issue, does **not** contain contoso's key/title |
| `DashboardSummary_WithoutTeamFilter_CountsOnlyAuthenticatedWorkspace` | AC-302 | every count/group excludes contoso |
| `ListIssues_WithForeignTeamFilter_IsDenied` | AC-303 | 403, `WORKSPACE_ACCESS_DENIED`, empty body |
| `GetIssue_ForeignId_IsDenied` | AC-304 | 403, no title/description in body |
| `ChangeStatus_ForeignId_IsDenied_AndLeavesIssueUnchanged` | AC-305 | 403 **and** re-read via DbContext shows unchanged `Status`, `Version`, activity count |
| `Assign_ForeignId_IsDenied_AndLeavesIssueUnchanged` | AC-305 | as above |
| `AddComment_ForeignId_IsDenied_AndWritesNoComment` | AC-305 | 403 **and** zero new `Comments` rows |
| `CreateIssue_ForeignTeamId_IsDenied_AndLeavesSequenceUnchanged` | AC-306 | 403, no `Issues` row, `NextIssueNumber` unchanged |
| `ListArtifacts_OnAnotherWorkspacesIssue_IsDenied`, `AttachArtifact_ToAnotherWorkspacesIssue_IsDeniedAndAttachesNothing`, `RemoveArtifact_FromAnotherWorkspacesIssue_IsDeniedAndLeavesTheArtifactIntact` | AC-307 | 403 on list/attach/remove; no `Artifacts` row change |
| `ListLinks_OnAnotherWorkspacesIssue_IsDenied`, `CreateLink_BetweenTwoOfAnotherWorkspacesIssues_IsDeniedAndWritesNoLink`, `RemoveLink_FromAnotherWorkspacesIssue_IsDeniedAndLeavesTheLinkIntact` | AC-307 | 403 on list/create/remove; no `IssueLinks` row change |
| `GetIssue_FromAnotherWorkspace_IsDeniedIndistinguishablyFromAnUnknownId` | AC-308 | asserts a foreign id and an unknown id produce equal status **and** equal body; the shared `AssertDeniedAsync` then pins that same title-only shape on every other denial in the suite (see §18 deviation 4) |

Asserting the *absence of a state change*, not merely the status code, is what distinguishes this
suite from a routing test — a handler that 403s after writing would still pass a status-only check.

### 15.2 Unit

- `RestWorkspaceScopeTests` — in-workspace id resolves; foreign id throws; unknown id throws the
  **same** exception; null member id passes through.
- `IssueServiceTests` / `DashboardServiceTests` / `IssueLinkServiceTests` / `ArtifactServiceTests` —
  for each scoped method, seed two workspaces and assert that passing workspace A's id cannot reach
  workspace B's row. These need no host and no `HttpContext`, which is a direct benefit of Solution A.
- `BoardQueryServiceTests` — unchanged; already the reference for the shape being adopted.

### 15.3 Regression

- `dotnet test Anvilboard.slnx` — 320 existing tests must stay green.
- `Anvilboard.Agent.Tests` must pass **without modification** (argument reordering is not a behavior
  change). Any required edit is a red flag to investigate, not to accommodate.
- `SyncCoordinator` tests must pass unmodified (`AC-310`) — proof the ingestion escape hatch survived.
- `npm test` in `src/anvilboard-web` — 21 passing, expected unaffected (no client change).

## 16. Milestones & Task Breakdown

Dependency-ordered. Total ≈ 34 h, within `tech-design.md` §16 **M1**'s remaining allowance.

| # | Task | Files | Depends on | Est. |
|---|---|---|---|---|
| T1 ✅ | `WorkspaceScopedQueries.InWorkspace(...)` extension + unit test | `src/Anvilboard.Application/Issues/` | — | 1 h |
| T2 ✅ | `RestWorkspaceScope` (+ DI registration in `Program.cs`) mirroring `AgentWorkspaceScope` | `src/Anvilboard.Api/Authorization/` | — | 3 h |
| T3 ✅ | `RestWorkspaceScopeTests` — in-workspace / foreign / unknown / null-member | `src/Anvilboard.Api.Tests/Authorization/` | T2 | 2 h |
| T4 ✅ | `IssueService`: required leading `WorkspaceId` on the 6 methods; rename the unscoped sync overload to `UpsertFromExternalUnscopedAsync` | `src/Anvilboard.Application/Issues/IssueService.cs` | T1 | 4 h |
| T5 ✅ | `DashboardService`: required `WorkspaceId`, predicate before materialization | `src/Anvilboard.Application/Dashboard/` | T1 | 1 h |
| T6 ✅ | `IssueLinkService`: required `WorkspaceId` on all 3 methods | `src/Anvilboard.Application/Issues/IssueLinkService.cs` | T1 | 2 h |
| T7 ✅ | `IArtifactService`/`ArtifactService`: required `WorkspaceId` on all 4 methods; `EnsureIssueExistsAsync` gains the workspace predicate; **delete the stale "takes no WorkspaceId" `<remarks>`** | `src/Anvilboard.Application/Artifacts/` | T1 | 3 h |
| T8 ✅ | `IssueEndpoints`: resolve every id through `RestWorkspaceScope`, pass `scope.WorkspaceId` (9 routes) | `src/Anvilboard.Api/Endpoints/IssueEndpoints.cs` | T2, T4, T6 | 3 h |
| T9 ✅ | `DashboardEndpoints` + `ArtifactEndpoints` (4 routes) | `src/Anvilboard.Api/Endpoints/` | T2, T5, T7 | 2 h |
| T10 ✅ | `BoardAgentService` + `SyncCoordinator` + hook call sites: mechanical argument reordering | `src/Anvilboard.Agent/`, `src/Anvilboard.Application/Sync/` | T4–T7 | 2 h |
| T11 ✅ | `CrossWorkspaceIsolationEndpointTests` — AC-301…AC-308 | `src/Anvilboard.Api.Tests/Authorization/` | T8, T9 | 8 h |
| T12 ✅ | Service-level two-workspace unit tests | `src/Anvilboard.Application.Tests/` | T4–T7 | 3 h |
| T13 ✅ | Full-suite regression: `dotnet test Anvilboard.slnx` + `npm test`; confirm agent and sync tests unmodified | — | T11, T12 | 1 h |
| T14 ✅ | Canonical doc updates (§16.1) | `docs/**`, XML docs | T13 | 2 h |

**Critical path:** T1 → T4 → T8 → T11 → T13 → T14.

### 16.1 Canonical documents to update on completion

Doc-first discipline: these are edited **in place**; no parallel or `-v2` files, and all existing
`FR-*`/`NFR-*`/`AC-*`/`MAJ-*` IDs are preserved.

| Document | Edit |
|---|---|
| [`docs/audit-report.md`](../audit-report.md) | **MAJ-022** → `RESOLVED` with an evidence block in the style of the existing `MAJ-021` resolution; strike the residual clause of priority action **#4** |
| [`docs/features/workspace-authorization.md`](../features/workspace-authorization.md) | Status row (line 11): drop "the residual gap is REST/application query scoping"; record REST/application scoping as implemented; add `AC-301`–`AC-310` to the AC table; annotate AF-2 with the §7.7 403-vs-404 clarification |
| [`docs/features/overview.md`](../features/overview.md) | Row 1 status: `workspace-authorization` moves from `Partial` to `Implemented`; re-stamp the "Last verified" line |
| [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) | §16 **M1** row → `Implemented`; §16 **M4** row loses its MAJ-022 reference; re-stamp the "Last verified" blockquote |
| [`docs/anvilboard/srs.md`](../anvilboard/srs.md) | `FR-WS-001` AF-2: clarify that an id unresolvable inside the authorized workspace is a 403, and the reserved 404 applies to dependent entities *within* it |
| [`IArtifactService.cs`](../../src/Anvilboard.Application/Artifacts/IArtifactService.cs) | Delete the `<remarks>` claiming the service "takes no `WorkspaceId`" and defers to MAJ-001/MAJ-015 — false after T7 |
| [`ArtifactService.cs`](../../src/Anvilboard.Application/Artifacts/ArtifactService.cs) | `EnsureIssueExistsAsync` XML doc: replace "It does not scope the lookup to a caller's workspace" with the new behavior |
| [`AgentWorkspaceScope.cs`](../../src/Anvilboard.Agent/Authorization/AgentWorkspaceScope.cs) | Class doc says "the application services resolve entities by primary key alone" — no longer true; point at `RestWorkspaceScope` as the sibling implementation |
| [`docs/anvilboard/test-cases.md`](../anvilboard/test-cases.md) | Add the `AC-301`–`AC-308` cross-workspace REST cases to the `NFR-SEC-002` coverage set |

## 17. Open Questions & Decision Records

| ID | Question / Decision | Status | Resolution |
|---|---|---|---|
| DR-1 | 403 vs 404 for an unknown id on an ID-addressed route | Resolved | **403 `WORKSPACE_ACCESS_DENIED`**, identical to the foreign case. `FR-WS-001` AF-2 reserves 404 for entities missing *inside an already-authorized workspace*; an unresolvable id is not one. Returning 404 would recreate the existence oracle `AgentWorkspaceScope` was written to avoid, and would make REST diverge from CLI/MCP. See §7.7. |
| DR-2 | Required parameter vs ambient context vs global query filters | Resolved | **Required parameter (Solution A)** — the only option where omission is a compile error. §5.5. |
| DR-3 | `WorkspaceId` first or last in the signature | Resolved | **First.** A trailing optional parameter is skippable via a named argument, which is literally how the current leak is written (`ct: ct`). Matches `BoardQuery`. |
| DR-4 | What happens to the unscoped `UpsertFromExternalAsync`? | Resolved | **Renamed to `UpsertFromExternalUnscopedAsync`**, kept, and documented. Polling ingestion has no authenticated workspace. Renaming preserves the capability while making every call site self-documenting and greppable, so the exception is auditable rather than invisible. |
| DR-5 | Scope both at the endpoint and in the service? | Resolved | **Yes.** The endpoint check produces the correct status code; the service parameter is the enforceable guarantee. Each covers the other's weakness (§8.3, §11.5). |
| DR-6 | Include artifacts and links, beyond MAJ-022's named Locations? | Resolved | **Yes.** MAJ-022's Fix says "every entity lookup and aggregate." Closing four files while leaving five equivalent holes would close the finding without closing the vulnerability (§3.4). |
| OQ-1 | Should `RestWorkspaceScope` and `AgentWorkspaceScope` be unified into one shared type in `Anvilboard.Application`? | Open | Attractive (one implementation, one test suite) but they differ in exception type (`AgentRequestException` vs an HTTP `IResult`) and in how they obtain the actor. Deferred: duplicate now with cross-referencing docs; unify only if a third channel appears. |
| OQ-2 | Should `ActorContext` ever carry multiple workspaces? | Open | Out of scope (NG2). If multi-workspace membership is ever added, `RequireIssueAsync` becomes a set-membership test rather than an equality test — the design survives that change, which is one more argument for a single choke point. |
| OQ-3 | Wire `ErrorCatalogTranslator` into REST so endpoints stop hand-rolling `Results.Problem` switches? | Open | Out of scope here. Recorded because this plan touches three endpoint files that would otherwise be natural first adopters; doing it in the same change would conflate a security fix with a refactor. |

## 18. Appendix

### A. Glossary

| Term | Meaning |
|---|---|
| Workspace | The tenancy boundary. Owns teams, members, workflow states, and (transitively) issues. |
| `ActorContext` | The authenticated identity for one request: `MemberId`, `WorkspaceId`, `Role`, optional token grants. Never carries a secret. |
| Identifier scoping | Verifying that a caller-supplied entity id resolves inside the caller's authenticated workspace. The missing third check (§11.2). |
| Existence oracle | An API that discloses whether an entity exists by varying its denial response — e.g. 404 for absent vs 403 for foreign. |
| Escape hatch | A deliberately unscoped path (here: polling ingestion), named so it is auditable rather than accidental. |

### B. References

- [`docs/audit-report.md`](../audit-report.md) — MAJ-022; priority action #4
- [`docs/anvilboard/srs.md`](../anvilboard/srs.md) — `FR-WS-001` (AF-2), `NFR-SEC-002`
- [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) — §8.1, §11.2, §16 M1/M4
- [`docs/features/workspace-authorization.md`](../features/workspace-authorization.md)
- [`docs/features/overview.md`](../features/overview.md)

### C. Related documents

- [`docs/plans/agent-surface-authorization.md`](./agent-surface-authorization.md) — the CLI/MCP half
  of the same leak. Closing it (commit `e3e03a5`) is what left MAJ-022 as the residue; its
  `AgentWorkspaceScope` is this plan's direct template.
- [`docs/plans/backup-and-restore.md`](./backup-and-restore.md) — format precedent, and the source of
  the `DatabaseOperationMiddleware` that sits between authorization and the endpoints.

### D. Requirements Traceability

| Requirement | Acceptance criteria | Tasks | Tests |
|---|---|---|---|
| `FR-WS-001` AC(2) — "a valid actor cannot read or mutate a workspace for which it lacks permission" | AC-002, AC-301–AC-308 | T1–T9 | §15.1 full suite |
| `FR-WS-001` AF-2 — 403 with correlation id, no workspace data | AC-303, AC-304, AC-308 | T2, T8, T9 | `UnknownId_AndForeignId_ProduceIdenticalResponses` |
| `NFR-SEC-002` — 100% of cross-workspace attempts denied, no protected data | AC-301–AC-308 | T11, T12 | `CrossWorkspaceIsolationEndpointTests` |
| `FR-WRK-001`/`FR-WRK-003` — issue read/list/transition | AC-301, AC-305 | T4, T8 | §15.1, §15.2 |
| `FR-OPS-001` — audit-trail integrity (mutations cannot be attributed across tenants) | AC-305, AC-306 | T4, T8 | state-change assertions in §15.1 |
| MAJ-022 Fix — "apply it before every entity lookup and aggregate" | AC-301–AC-307, AC-309 | T1, T4–T9 | §15.2 per-service tests |
| MAJ-022 Fix — "two-workspace REST integration tests covering explicit foreign IDs and omitted filters" | AC-301, AC-302, AC-303 | T11 | `CrossWorkspaceIsolationEndpointTests` |
| NG3 — unauthenticated ingestion (webhook/poller) keeps its trusted-workspace derivation and is not regressed | AC-310 | T7, T13 | existing `SyncCoordinator`/`WebhookEndpoints` suites, unmodified |


## 18. Delivery Notes

Recorded after implementation so the plan stays the truthful record of what shipped.

**Four deviations from this plan as written:**

1. **`WorkspaceScopedQueries` is `public`, not `internal`** (§6 proposed `internal`). `Anvilboard.Application.Dashboard`,
   `.Artifacts`, and `Anvilboard.Api.Authorization` all need the predicate. `public` avoids an
   `InternalsVisibleTo` that would have punched a hole across the API/Application boundary purely
   for visibility.
2. **Two leaks were found that the audit and this plan both missed**, and were fixed here rather
   than deferred, because both are the same finding reached by a different route:
   - `IssueLinkService.CreateLinkAsync` compared the two issues' workspaces *to each other*. Two
     issues drawn entirely from a foreign workspace satisfied that check. Both ids must now appear
     in a single `.InWorkspace(db, workspaceId)` result.
   - `ArtifactService.RemoveArtifactAsync` never validated the issue at all; it now calls
     `EnsureIssueExistsAsync` first.
3. **`AssignAsync`/`AddCommentAsync` now fail closed.** `RecordAndDispatchAsync` accepts an
   optional workspace and otherwise resolves one lazily at publication time inside a
   swallow-everything `try`. These two were the last callers omitting it; all six now pass it, so
   the fallback is unreachable from any scoped path. The visible consequence: if an issue's team has
   been deleted, `AddCommentAsync` rejects at the scoped lookup instead of committing a comment
   whose tenant cannot be determined. `IssueServiceRealtimePublicationTests` was inverted to pin
   the new behavior.
4. **`AC-308` is pinned by one dedicated test plus a shared assertion, not by a `[Theory]`.** §15.1
   proposed a single `UnknownId_AndForeignId_ProduceIdenticalResponses` parameterized over every
   ID-addressed route. What shipped is `GetIssue_FromAnotherWorkspace_IsDeniedIndistinguishablyFromAnUnknownId`,
   which compares the two responses directly, plus `AssertDeniedAsync` — the single helper every
   denial in the suite routes through, asserting 403, the `WORKSPACE_ACCESS_DENIED` title, and the
   absence of `detail`/`data`. A route cannot be denied in this suite without producing that exact
   body, so the indistinguishability property holds across all 15 cases; the parameterized form
   would have restated it per route without testing anything the helper does not already pin.

**Verification beyond the suite passing:** the isolation suite was mutation-tested — reverting
`IssueEndpoints` `GET /{id}` to `new IssueId(id)` *and* stripping `.InWorkspace` from
`IssueService.GetAsync` makes `GetIssue_FromAnotherWorkspace_IsDeniedIndistinguishablyFromAnUnknownId`
fail. Both layers had to be broken for the test to go red, which is the defence in depth §8 intends.

`Anvilboard.Agent.Tests` passed **40/40 unmodified** throughout, confirming §15.3: the agent surface
already had the correct shape, so this was a re-signature there rather than a behavior change.
