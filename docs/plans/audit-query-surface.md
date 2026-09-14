# Implementation Plan: Audit Query Surface

> Feature-level technical design and execution plan for the former **Major** gap in the
> `audit-and-recovery` component — the P0 SRS requirement gap closed by this delivered plan.
> Canonical chain: [`prd.md`](../anvilboard/prd.md) → [`srs.md`](../anvilboard/srs.md) →
> [`tech-design.md`](../anvilboard/tech-design.md) → [`audit-and-recovery.md`](../features/audit-and-recovery.md).

## 1. Document Information

| Field | Value |
|---|---|
| Document | Implementation Plan — Audit Query Surface |
| Feature component | [`audit-and-recovery`](../features/audit-and-recovery.md) |
| Milestone | [`tech-design.md`](../anvilboard/tech-design.md) §16 **M3: Unified Board & Audit** |
| Audit findings | [`audit-report.md`](../audit-report.md) **MAJ-018** (primary); **MIN-010** (narrowed, not closed) |
| SRS refs | `FR-OPS-001` (primary), `NFR-SEC-001` + `FR-AUT-001` + `FR-AUT-003` (touched) |
| Acceptance criteria | `FR-OPS-001 AC2`, `FR-OPS-001 AC3`, `FR-OPS-001 AC4` |
| Status | **Implemented** — delivered with the shared application query service, REST and agent adapters, and automated coverage. |
| Created | 2026-09-13 |

## 2. Why this feature was selected

The backlog lives in the feature index and the audit report, not in GitHub issues. Selecting "the
next feature" therefore means selecting the largest *verified* gap. The evidence:

| Signal | Finding |
|---|---|
| Open GitHub issues | `gh issue list --state open` → only `#41` "Dependency Dashboard" (Renovate bot). No feature work is tracked there. |
| Existing plans | All 7 documents in [`docs/plans/`](.) carry a `Status` row of **Implemented**/**Delivered**. No plan is unfinished, so a new one is genuinely required. |
| Unresolved Critical findings | **None.** CRIT-001, CRIT-002 and CRIT-003 are all marked RESOLVED. |
| Unresolved Major findings (at selection time) | 5 open: MAJ-006, MAJ-009, MAJ-014, **MAJ-018**, MAJ-020. MAJ-018 is now resolved by this plan; 4 major findings remain open. |
| Feature-spec status (at selection time) | `audit-and-recovery` was the **only** feature spec still marked `Partial` for a missing *capability* rather than polish, and its `Implementation Plan` row pointed at the already-delivered `backup-and-restore.md` — i.e. the component had no active plan. It is now implemented; see [`audit-and-recovery.md`](../features/audit-and-recovery.md). |
| Requirement priority | MAJ-018 was the only open finding whose gap was named by a **P0** SRS requirement (`FR-OPS-001` AC3) *and* already specified down to the API name in the feature spec. |

### 2.1 Why not the other open findings

| Finding | Why deferred |
|---|---|
| MAJ-006 threaded comments | Requires a schema *and* UX decision; the audit report itself offers "or re-scope the spec" as an acceptable resolution, so the requirement is not settled. |
| MAJ-009 optimistic concurrency | An audit-and-harden sweep across existing mutations, not a feature slice. Better executed once as a dedicated pass. |
| MAJ-014 outbound plugin events | A substantial new pub/sub subsystem; larger than one plan and not blocking a P0 requirement. |
| MAJ-020 artifact lifecycle hooks | Already **partially** resolved — audit emission is closed; only the expansion hook remains. |
| MIN-004 / MIN-010 | Minor severity. MIN-010 is *narrowed* by this plan as a side effect (§14, `DR-AUD-004`). |

### 2.2 Verified current state

Claims below were checked against the working tree at `4707b41`, not taken from the audit report.

| Claim | Verification |
|---|---|
| `IAuditService` is write-only | [`src/Anvilboard.Application/Authorization/IAuditService.cs`](../../src/Anvilboard.Application/Authorization/IAuditService.cs) declares exactly two members — `RecordAuthorizationDecisionAsync` and `RecordAsync`. There is no query method, and no `IAuditQueryService` exists anywhere in `src/`. |
| Audit rows are unreachable over any channel | No `AuditEndpoints.cs` in `src/Anvilboard.Api/Endpoints/`, and `Api/Program.cs` maps 11 endpoint groups, none of them audit. No audit operation in [`BoardAgentService`](../../src/Anvilboard.Agent/BoardAgentService.cs). Outside tests and migrations, `db.AuditEvents` has exactly **one** consumer in the whole solution: `db.AuditEvents.Add(...)` in `AuditService`. Audit history is currently readable only by opening the SQLite file. |
| The permission already exists and is dangling | [`Permission.ReadAudit`](../../src/Anvilboard.Domain/Permission.cs) is declared ("Read recorded audit events") and granted to `Administrator` — and only `Administrator` — by `RolePermissionMap`. A project-wide search finds its only two occurrences are that declaration and that grant: **zero consumers**. The authorization scaffolding for this feature is pre-built and unused. |
| The index this query needs is already present | [`AuditEventConfiguration`](../../src/Anvilboard.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs) declares `HasIndex(a => new { a.WorkspaceId, a.OccurredAt })`, shipped as `IX_AuditEvents_WorkspaceId_OccurredAt` in the `AddAuditEvents` migration. |
| The feature spec already names the missing API | [`audit-and-recovery.md`](../features/audit-and-recovery.md) lists `IAuditService.QueryAsync` under **Scope → Included**, defines an `AuditQueryResult` output, and makes "Workspace-Scoped Audit Query" Core Responsibility #2. |

The conclusion is unusually well-supported: the domain model, the index, the permission, and the
role grant are all already in place. What is missing is the read path that consumes them.

## 3. Overview

### 3.1 Background

Every mutating component in Anvilboard already writes an immutable `AuditEvent`: who, in which
workspace, over which channel, against which target, when, with a redacted result summary and a
correlation ID. `FR-OPS-001` requires four things of that trail. Three are met — records carry the
full field set (AC1), nothing updates or deletes them (AC2, structurally, because no such code path
exists), and secrets are scrubbed at write time by `SecretRedactor.Scrub` (AC4).

AC3 — *"Audit queries are workspace-scoped and permission-gated"* — is vacuously satisfied today
only because **there are no audit queries at all**. An administrator or compliance reviewer who
needs to answer "what did this actor change last Tuesday?" must stop the host and open the SQLite
file by hand. For a product whose pitch is a single-file, self-hosted tracker that an operator can
actually reason about, an unreadable audit log is a hole in the accountability story rather than a
missing convenience.

### 3.2 Goals

| # | Goal |
|---|---|
| G1 | Expose a generic, filterable audit query over actor, time range, target, action, and channel. |
| G2 | Make workspace scoping structural — no caller-supplied workspace parameter anywhere on the path. |
| G3 | Gate the read on `Permission.ReadAudit`, activating the already-declared permission. |
| G4 | Serve the query over both channels the product supports for reads: REST and the agent surface. |
| G5 | Keep the trail provably append-only — the new surface must introduce no write, update, or delete path. |

### 3.3 Non-Goals

| # | Non-goal | Rationale |
|---|---|---|
| N1 | An Angular audit-log UI | The feature spec assigns UI to `anvilboard-web` and excludes administration UI from this component. Tracked as a follow-up in §13.2. |
| N2 | Export (CSV/JSON download) of audit history | Not required by `FR-OPS-001`; the backup path already produces a full copy. |
| N3 | Retention/pruning policy | Pruning would be a *write* path against an append-only table and needs its own requirement. |
| N4 | Full-text search over `ResultSummary` | `ResultSummary` is free-form prose; indexing it is a separate performance problem and a re-derivation risk for redaction. |
| N5 | Changing `AuditEvent`'s shape or the redaction deny-list | The write path is implemented and tested; this plan is read-only by construction. |
| N6 | Closing MIN-010 | This plan avoids *widening* the enum-casing inconsistency and narrows it slightly (`DR-AUD-004`), but the global sweep is separate. |

### 3.4 Scope

**In scope**

- `IAuditQueryService` + `AuditQueryService` in `Anvilboard.Application/Auditing/`
- `AuditQuery` filter record, `AuditEventDto`, `AuditQueryResult` page record, `AuditQueryException`
- Cursor paging consistent with `ActivityQueryService` (default 50, maximum 200)
- `GET /api/audit-events` under `RequirePermission(Permission.ReadAudit)`
- Agent operation `list-audit-events` under `[RequiresAgentPermission(Permission.ReadAudit)]`
- DI registration in `Anvilboard.Application/ServiceCollectionExtensions.cs`
- Tests in `Anvilboard.Application.Tests`, `Anvilboard.Api.Tests`, `Anvilboard.Agent.Tests`
- Canonical documentation updates (§13.1)

**Out of scope** — everything in §3.3, plus any EF migration (see §10 for why none is needed).

### 3.5 User Scenarios

| # | Scenario |
|---|---|
| S1 | An administrator investigating an unexpected workflow-state change filters by `targetType=workflow-state` over the last 24 hours and sees each change with its actor and channel. |
| S2 | A compliance reviewer answering "what did this departing member touch?" filters by `actorId` across a date range and pages through the result. |
| S3 | An agent, before proposing a bulk change, calls `list-audit-events` filtered by `action` to check whether a human already made the change. |
| S4 | A `Contributor` calls `GET /api/audit-events` and receives `403 WORKSPACE_ACCESS_DENIED` — the role has no `ReadAudit` grant. |
| S5 | A caller authenticated against workspace A cannot see workspace B's events under any filter combination, because workspace is never an input. |

### 3.6 Acceptance Criteria

| ID | Criterion | Maps to |
|---|---|---|
| `AC-AUD-001` | `QueryAsync` returns only events whose `WorkspaceId` equals the caller's authenticated workspace. | `FR-OPS-001 AC3`, G2 |
| `AC-AUD-002` | Results are ordered newest-first and are stable across pages when many events share one `OccurredAt`. | G1 |
| `AC-AUD-003` | Filtering by `actorId` returns only that actor's events. | `FR-OPS-001 AC3`, G1 |
| `AC-AUD-004` | Filtering by `occurredAfter`/`occurredBefore` returns only events inside the half-open range `[after, before)`. | G1 |
| `AC-AUD-005` | Filtering by `targetType` + `targetId` returns only events against that entity. | G1 |
| `AC-AUD-006` | Filtering by `action` returns only that action; filtering by `channel` returns only that channel. | G1 |
| `AC-AUD-007` | Multiple filters combine with AND. | G1 |
| `AC-AUD-008` | `limit < 1` or `limit > 200` throws `VALIDATION_FAILED`; omitted `limit` defaults to 50. | §7.3 |
| `AC-AUD-009` | A malformed cursor throws `VALIDATION_FAILED`. | §7.3 |
| `AC-AUD-010` | `NextCursor` is non-null exactly when more rows remain, and paging through it visits every row once with no duplicates or gaps. | `AC-AUD-002` |
| `AC-AUD-011` | `occurredAfter > occurredBefore` throws `VALIDATION_FAILED` rather than silently returning empty. | §7.3 |
| `AC-AUD-016` | A filter matching more than `ScanCeiling` rows throws `VALIDATION_FAILED` rather than returning a truncated page. | §8.3 |
| `AC-AUD-012` | `GET /api/audit-events` answers `403` for a role without `ReadAudit`, and `200` for `Administrator`. | `FR-OPS-001 AC3`, G3 |
| `AC-AUD-013` | The agent operation `list-audit-events` is discovered by the catalog, declares `ReadAudit`, is `IsIdempotent = true`, and returns `AgentResponse<AuditQueryResult>`. | G4 |
| `AC-AUD-014` | The returned `ResultSummary` is the stored (already-redacted) value; the query path performs no re-derivation. | `FR-OPS-001 AC4`, N5 |
| `AC-AUD-015` | `IAuditQueryService` exposes no member that writes, updates, or deletes an `AuditEvent`. | `FR-OPS-001 AC2`, G5 |

## 4. System Context

```
  REST caller                         Agent (CLI / MCP)
      |                                     |
      | GET /api/audit-events               | list-audit-events
      v                                     v
  WorkspaceAuthorizationMiddleware      WorkspaceAuthorizationPolicy
  (reads RequiresPermissionAttribute)   (reads RequiresAgentPermissionAttribute)
      |  Permission.ReadAudit               |  Permission.ReadAudit
      v                                     v
  AuditEndpoints ---- workspace ----> IAuditQueryService <---- workspace ---- BoardAgentService
   (RestWorkspaceScope)                     |                                 (AgentWorkspaceScope)
                                            v
                                    AnvilboardDbContext
                                      db.AuditEvents
                                   (read-only, AsNoTracking)
```

Both channels enter the *same* application service, which is the rule the tech design sets for
every capability (§11.2 single enforcement point): permission is proven by the channel's
authorization layer before the service is reached, and the service never re-authorizes. The
workspace identifier is supplied by the channel's scope object, which derives it from the
authenticated actor — it is never read from a query string, route value, or agent argument.

## 5. Solution Design

### 5.1 Solution A (Recommended) — Dedicated read service, DB-filtered + in-memory ordering, offset cursor

A new `IAuditQueryService` sits beside the existing write-only `IAuditService`. It builds an
`IQueryable<AuditEvent>` pinned to the caller's workspace, applies each supplied filter as a
translatable `Where` clause, materializes `limit + 1` rows, then orders in memory by
`OccurredAt` descending with an `Id` tiebreak and slices the page.

This mirrors [`ActivityQueryService`](../../src/Anvilboard.Application/Activity/ActivityQueryService.cs)
line for line in structure, which matters because that service exists specifically to work around a
SQLite provider limitation this feature hits too (§7.4).

### 5.2 Solution B (Alternative) — Add `QueryAsync` to the existing `IAuditService`

The feature spec's Scope section literally names `IAuditService.QueryAsync`, so the most direct
reading is to add the method to the existing interface.

The problem is that `IAuditService` has a second implementation, `NoOpAuditService`, whose entire
purpose is to *silently discard* writes. A `QueryAsync` on that type has no honest implementation:
it must either return an empty page — turning a swapped-in no-op auditor into a surface that
reports "nothing ever happened" to a compliance reviewer — or throw, which makes the interface
partially unimplementable. Merging a read concern into a write-sink abstraction also breaks the
repository's own separation (`IssueService` writes, `IBoardQueryService` reads; the activity feed is
a read service distinct from the components that emit activity).

### 5.3 Comparison Matrix

| Criterion | A — dedicated read service | B — method on `IAuditService` |
|---|---|---|
| Honest for `NoOpAuditService` | Yes — the no-op type is untouched | **No** — must fake or throw |
| Matches repo precedent | Yes — `IBoardQueryService`, `IActivityQueryService` | No |
| `AC-AUD-015` (no write path on the read type) provable by inspection | Yes | No — read and write share one type |
| Literal match to feature-spec wording | No (recorded as `DR-AUD-001`) | Yes |
| DI / consumer churn | One new registration | None |
| Risk of a read caller acquiring write capability | None | Every consumer of the query gains `RecordAsync` |

### 5.4 Decision & Rationale

**Solution A.** The deciding factor is `NoOpAuditService`: Solution B has no truthful
implementation for it, and the failure mode — an audit query that confidently returns "no events"
— is precisely the kind of silent wrong answer an audit trail exists to prevent. The deviation from
the feature spec's literal wording is recorded as `DR-AUD-001` and the spec's Interfaces section is
updated accordingly in §13.1, so the canonical chain stays consistent rather than being quietly
contradicted.

## 6. Architecture Design

```
Anvilboard.Domain
  Permission.ReadAudit ............................. exists; gains its first consumers
  AuditEvent, AuditChannel ......................... unchanged

Anvilboard.Application/Auditing/
  IAuditQueryService.cs ...... NEW  interface + AuditQuery, AuditEventDto,
                                    AuditQueryResult, AuditQueryException
  AuditQueryService.cs ....... NEW  the only implementation
  AuditService.cs ............ unchanged (write path)

Anvilboard.Application/ServiceCollectionExtensions.cs
  AddScoped<IAuditQueryService, AuditQueryService>() ......... NEW line

Anvilboard.Api/Endpoints/
  AuditEndpoints.cs .......... NEW  MapGroup("/api/audit-events").RequirePermission(ReadAudit)
Anvilboard.Api/Program.cs
  app.MapAuditEndpoints() .................................... NEW line

Anvilboard.Agent/BoardAgentService.cs
  ListAuditEventsAsync() ..... NEW  [AgentOperation("list-audit-events", IsIdempotent = true)]
                                    [RequiresAgentPermission(Permission.ReadAudit)]
```

No new project, no new cross-project reference, no change to any existing type's behavior. The
`Anvilboard.Infrastructure` project is untouched.

## 7. Technology Stack & Conventions

### 7.1 Stack

.NET 9, EF Core over SQLite, ASP.NET Core minimal APIs, `DotNetAgentSurface` for the CLI/MCP
catalog, xUnit with an in-memory SQLite fixture. Nothing new is introduced.

### 7.2 Naming Conventions

Follows the established pair-naming: `I{Area}QueryService` / `{Area}QueryService`, a `{Area}Query`
input record, a `{Area}QueryResult` page record, and a feature-local `{Area}QueryException` carrying
an `ErrorCode` string — exactly as `IBoardQueryService`/`BoardQuery`/`BoardQueryResult` and
`IActivityQueryService`/`ActivityPage`/`ActivityQueryException` already do.

### 7.3 Parameter Validation & Input Parsing

| Input | Rule | On violation |
|---|---|---|
| `limit` | `null` → 50; otherwise `1 <= limit <= 200` | `VALIDATION_FAILED` |
| `cursor` | `null`/blank → offset 0; otherwise base64 of `offset:{n}` with `n >= 0` | `VALIDATION_FAILED` |
| `occurredAfter` / `occurredBefore` | Both optional; if both present, `after < before` | `VALIDATION_FAILED` |
| `actorId`, `action`, `targetType`, `targetId` | Optional; whitespace-only is treated as absent, not as a match for empty string | — |
| `channel` | Optional; must parse to an `AuditChannel` member | `VALIDATION_FAILED` |
| workspace | **Not an input.** Supplied by `RestWorkspaceScope` / `AgentWorkspaceScope`. | n/a |

Treating whitespace-only filters as absent rather than as literal matches is deliberate: a caller
sending `?action=` should get an unfiltered page, not an empty one, and every stored `Action` is
non-empty by the column's `IsRequired()` constraint so a blank filter could only ever match nothing.

### 7.4 Boundary Values & Edge Cases

| Case | Behavior |
|---|---|
| **SQLite cannot order by `DateTimeOffset`** | The provider stores `DateTimeOffset` as TEXT carrying an offset, so a lexical `ORDER BY` misorders rows written from different offsets. Filtering happens in the database against the indexed column; **ordering happens after materialization, and before `Skip`/`Take`**, exactly as `ActivityQueryService`, `BoardQueryService` and `DashboardService` already do. |
| **Ties on `OccurredAt`** | `OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id.Value)` — mandatory. Several audit events written inside one mutation share a timestamp to the tick; without the tiebreak an offset cursor would repeat or skip rows across a page boundary (`AC-AUD-010`). |
| **Filter matches more than `ScanCeiling` (5,000) rows** | Throws `VALIDATION_FAILED` asking the caller to narrow the time range, rather than returning a silently truncated page (§8.3). |
| Empty workspace / no matches | Empty `Events` list, `NextCursor = null`, `HasMore = false`. Never an error. |
| Exactly `limit` rows remain | `NextCursor = null` — detected by taking `limit + 1` and observing only `limit` came back. |
| Time range boundaries | Half-open `[occurredAfter, occurredBefore)`, so adjacent ranges tile without double-counting a boundary event. |
| Cursor past the end | Empty page, `NextCursor = null`. Not an error — a client paging a shrinking view should not fault. |

### 7.5 Business Logic Rules

| # | Rule |
|---|---|
| BR1 | Workspace scoping is unconditional and cannot be widened by any filter combination. |
| BR2 | The service performs no authorization; the channel's authorization layer has already proven `ReadAudit` (§11.2 single enforcement point). |
| BR3 | `ResultSummary` is returned as stored. Redaction is a write-time invariant and is never re-applied or re-derived on read. |
| BR4 | All queries are `AsNoTracking()` — the service must not be able to mutate a row even accidentally. |
| BR5 | Filters combine with AND only. No OR/negation surface, keeping every clause index-friendly and translatable. |

### 7.6 Error Handling Strategy

`AuditQueryException(errorCode, message)` mirrors `ActivityQueryException`. REST maps it through
the shared `ErrorCodeCatalog.HttpStatusFor`; the agent surface maps it through the existing
`ErrorCatalogTranslator`. No new response envelope and no new error code are introduced.

### 7.7 Error Catalog & Traceability

| Code | HTTP | Raised when | Already in catalog |
|---|---|---|---|
| `VALIDATION_FAILED` | 400 | Bad `limit`, malformed cursor, inverted time range, unparseable channel, or a filter matching more than `ScanCeiling` rows (§8.3) | Yes |
| `WORKSPACE_ACCESS_DENIED` | 403 | Caller lacks `ReadAudit` (raised by the authorization layer, not this service) | Yes |
| `AUTHENTICATION_REQUIRED` | 401 | No authenticated actor (raised by middleware) | Yes |

## 8. Detailed Design

### 8.1 Contracts

```csharp
namespace Anvilboard.Application.Auditing;

/// <summary>Read-only, workspace-scoped access to the append-only audit trail.</summary>
public interface IAuditQueryService
{
    Task<AuditQueryResult> QueryAsync(
        WorkspaceId workspaceId, AuditQuery query, CancellationToken ct = default);
}

public sealed record AuditQuery(
    string? ActorId = null,
    DateTimeOffset? OccurredAfter = null,
    DateTimeOffset? OccurredBefore = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Action = null,
    AuditChannel? Channel = null,
    int? Limit = null,
    string? Cursor = null);

public sealed record AuditEventDto(
    Guid Id,
    string ActorId,
    AuditChannel Channel,
    string Action,
    string TargetType,
    string TargetId,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string ResultSummary);

public sealed record AuditQueryResult(
    IReadOnlyList<AuditEventDto> Events,
    bool HasMore,
    string? NextCursor,
    AuditQuery AppliedQuery);

public sealed class AuditQueryException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
```

`AppliedQuery` echoes the normalized query back — the same affordance `BoardQueryResult` provides —
so a client can see which defaults were applied (notably the resolved `Limit`) without guessing.

`Channel` is carried as the **live `AuditChannel` enum**, not a flattened string. See `DR-AUD-004`.

### 8.2 Core Workflow — `QueryAsync`

1. Validate `Limit` (§7.3); resolve to `DefaultLimit = 50`, bounded by `MaximumLimit = 200`.
2. Decode `Cursor` to an integer offset; a `FormatException` becomes `VALIDATION_FAILED`.
3. Validate the time range if both bounds are present.
4. Start from `db.AuditEvents.AsNoTracking().Where(e => e.WorkspaceId == workspaceId)` — the
   workspace predicate is applied *first and unconditionally*, before any caller-supplied filter,
   so no later clause can widen it.
5. Append one translatable `Where` per supplied filter:
   `ActorId`, `OccurredAt >= after`, `OccurredAt < before`, `TargetType`, `TargetId`, `Action`,
   `Channel`.
6. Materialize the filtered set with `.Take(ScanCeiling + 1).ToListAsync(ct)` (see §8.3).
7. Order **in memory**, then page:
   `.OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id.Value).Skip(offset).Take(limit + 1)`.
8. `hasMore = rows.Count > limit`; keep the first `limit`.
9. Project to `AuditEventDto`; return with `NextCursor = hasMore ? Encode(offset + count) : null`
   and the normalized `AppliedQuery`.

Step 7's sequencing is load-bearing and is the single easiest thing to get wrong here: the ordering
**must** be applied before `Skip`/`Take`, because the database cannot order the set (§7.4) and
therefore returns it in an arbitrary order. Skipping first would page through an unordered set and
then sort each page in isolation, producing a globally wrong sequence that still *looks* sorted
within every individual page. This is exactly the shape
[`ActivityQueryService`](../../src/Anvilboard.Application/Activity/ActivityQueryService.cs) uses —
materialize, order, then `Skip(offset).Take(limit + 1)` — and this plan follows it.

Unlike `ActivityQueryService`, there is **no actor-name join**. `AuditEvent.ActorId` is an opaque
string (e.g. `member:{guid}`, but also non-member principals for system-channel events), so
resolving it to a display name would be a lossy guess. The raw attributed identity is what an audit
trail should return; display-name resolution is a presentation concern for the follow-up UI (§13.2).

### 8.3 The materialization problem, and the scan ceiling

Ordering in memory means the filtered set must be materialized, and this is where audit history
differs materially from the activity feed. `ActivityQueryService` materializes one *issue's* events
— inherently small and naturally bounded. `AuditEvents` accumulates a row for **every mutation in
the workspace, forever**, and is the fastest-growing table in the product. Copying its paging
strategy without accounting for that would mean loading an entire workspace's audit history into
memory to serve a 50-row page.

The mitigation is a hard **`ScanCeiling = 5_000`** on the materialized set (step 6), combined with
the `{WorkspaceId, OccurredAt}` index doing the narrowing in the database:

- The ceiling is applied as `Take(ScanCeiling + 1)`. If the extra row comes back, the filter matched
  more history than can be ordered correctly, and the service throws `VALIDATION_FAILED` with a
  message directing the caller to narrow the time range — **it does not silently return a
  partial, possibly mis-ordered page**. Failing loudly is the right behavior for an audit surface:
  a truncated answer to "what did this actor do?" is worse than an error.
- Because `occurredAfter`/`occurredBefore` are index-backed, narrowing the window is both the
  natural remedy and a cheap one.
- `ScanCeiling / MaximumLimit = 25` full pages, which comfortably covers the interactive
  investigation scenarios in §3.5 while bounding worst-case memory at a few thousand rows.

Recorded as `DR-AUD-003`. The durable fix — a sortable, index-friendly UTC column so ordering and
paging can both run in the database and the ceiling can be dropped — is `DR-AUD-006`/F6.

### 8.4 Cursor stability note

The offset cursor is safe here specifically because the table is **append-only**: a page boundary
cannot be invalidated by an update or delete, since no such code path exists (`AC-AUD-015`). The
only concurrent change is an *insert*, which lands at the newest end and can at worst shift a
forward-paging client by one row. `AC-AUD-010` asserts the no-duplicate/no-gap property against a
static fixture, which is the guarantee actually being claimed.

## 9. API Design

### 9.1 Overview

| Method | Route | Permission | Success |
|---|---|---|---|
| `GET` | `/api/audit-events` | `Permission.ReadAudit` | `200 AuditQueryResult` |

Declared as `app.MapGroup("/api/audit-events").WithTags("Audit").RequirePermission(Permission.ReadAudit)`.
The endpoint does **not** call `AuthorizeAsync` itself — `WorkspaceAuthorizationMiddleware` reads
the `RequiresPermissionAttribute` metadata, per tech-design §11.2.

Gating on `ReadAudit` rather than `ManageBackupRestore` follows the precedent set when
`ReadIntegrationHealth` was split out of `ManageIntegrations`: reading the accountability record
should not require holding the capability to overwrite the database.

### 9.2 Request Contract

| Query parameter | Type | Notes |
|---|---|---|
| `actorId` | string? | Exact match on the stored opaque actor identity |
| `occurredAfter` | date-time? | Inclusive lower bound |
| `occurredBefore` | date-time? | Exclusive upper bound |
| `targetType` | string? | e.g. `issue`, `workflow-state`, `integration` |
| `targetId` | string? | |
| `action` | string? | e.g. `issue.status-changed` |
| `channel` | enum? | `WEB` \| `REST` \| `CLI` \| `MCP` \| `SYSTEM` |
| `limit` | int? | 1–200, default 50 |
| `cursor` | string? | Opaque; from the previous response's `nextCursor` |

There is deliberately **no `workspaceId` parameter** (BR1) — the workspace comes from the
authenticated actor, so the route cannot be used to read another tenant's history.

### 9.3 Agent Contract

```csharp
[AgentOperation("list-audit-events",
    "Lists workspace audit history filtered by actor, time range, target, action, or channel",
    Category = "audit", IsIdempotent = true)]
[RequiresAgentPermission(Permission.ReadAudit)]
public async Task<AgentResponse<AuditQueryResult>> ListAuditEventsAsync(...)
```

`IsIdempotent = true` and no `idempotencyKey` parameter, matching every other read operation. The
operation name must be added to `OperationsWithoutIdempotencyKey` in
[`AgentCatalogInvariantsTests`](../../src/Anvilboard.Agent.Tests/AgentCatalogInvariantsTests.cs),
otherwise the catalog invariant test fails — which is the intended fail-closed behavior.

## 10. Data & Storage Design

**No schema change and no EF migration.**

The existing `HasIndex(a => new { a.WorkspaceId, a.OccurredAt })` covers the mandatory workspace
predicate and the time-range filters, which are the only clauses on the hot path — and which are
also the filters that keep a query under the `ScanCeiling` (§8.3). The remaining filters (`ActorId`,
`TargetType`/`TargetId`, `Action`, `Channel`) are residual predicates applied within an already
workspace-narrowed set.

Note the index is used here **only for filtering, never for ordering** — the `OccurredAt` component
narrows the range in SQL, but the sort still happens in memory because the provider cannot translate
it (§7.4). Removing that limitation is F6/`DR-AUD-006`.

Adding a speculative index on `ActorId` or `(TargetType, TargetId)` was considered and rejected:
`AuditEvents` is a write-heavy, append-only table in a single-file SQLite deployment, so every extra
index is a permanent cost on every mutation in the product, paid to speed up an occasional
administrative read over one workspace's rows. Recorded as `DR-AUD-002`; revisit only with a
measured slow query.

`AuditChannel` is already persisted via a value converter as an UPPERCASE string, so filtering by
`Channel` translates to a plain string equality in SQL.

## 11. Security Design

| Concern | Control |
|---|---|
| Cross-workspace disclosure | Workspace is never an input (BR1); the predicate is applied before any caller-supplied filter. Covered by `AC-AUD-001` and the existing `CrossWorkspaceIsolationEndpointTests` pattern. |
| Over-broad access | `Permission.ReadAudit`, held only by `Administrator` in `RolePermissionMap`. `Contributor`/`Coordinator` get `403`. |
| Secret leakage | None introduced: `ResultSummary` was scrubbed by `SecretRedactor.Scrub` at write time and is returned verbatim (BR3, `AC-AUD-014`). The query path adds no new field to the payload. |
| Tamper resistance | The read service exposes no mutating member (`AC-AUD-015`), and `AsNoTracking()` means the returned entities are not change-tracked, so no accidental `SaveChangesAsync` elsewhere could persist a modification. |
| Existence oracle | Not applicable — no ID-addressed route is added; unmatched filters return an empty page, which reveals nothing about other workspaces. |
| Enumeration / cost | `MaximumLimit = 200` bounds any single response. |

## 12. Testing Strategy

| Project | File | Covers |
|---|---|---|
| `Anvilboard.Application.Tests` | `Audit/AuditQueryServiceTests.cs` (new, beside the existing `Audit/AuditServiceTests.cs`) | `AC-AUD-001` … `AC-AUD-011`, `AC-AUD-014`, `AC-AUD-015`, `AC-AUD-016` |
| `Anvilboard.Api.Tests` | `AuditEndpointTests.cs` (new, via `ApiFactory`) | `AC-AUD-012`, routing, DI resolution, JSON shape, `channel` casing |
| `Anvilboard.Agent.Tests` | `AuditOperationTests.cs` (new) + an entry in `AgentCatalogInvariantsTests` | `AC-AUD-013` |

The service tests use an in-memory SQLite fixture created via `EnsureCreatedAsync` (the pattern used
by `ActivityQueryServiceTests` and `BoardQueryServiceTests`) so the real column constraints and the
`AuditChannel` value converter are genuinely exercised rather than mocked.

`AC-AUD-002`/`AC-AUD-010` are the tests that matter most and need deliberate fixtures: seed several
events sharing one `OccurredAt`, plus events written with **differing UTC offsets**, then page
through with `limit = 2` and assert the concatenation of pages equals the expected order with no
duplicates. Without the differing-offset rows the SQLite ordering hazard (§7.4) would not be caught.

`AC-AUD-015` is asserted structurally by reflecting over `IAuditQueryService`'s members, so a future
addition of a write method fails the build rather than silently eroding the append-only guarantee.

`AC-AUD-016` needs the `ScanCeiling` to be injectable (or `internal` + `InternalsVisibleTo`) so the
test can seed a handful of rows against a ceiling of 2 rather than seeding 5,001 rows. Prefer an
options/constructor value over a compile-time constant for exactly this reason.

Verification commands:

```powershell
dotnet test src\Anvilboard.Application.Tests\Anvilboard.Application.Tests.csproj
dotnet test src\Anvilboard.Api.Tests\Anvilboard.Api.Tests.csproj
dotnet test src\Anvilboard.Agent.Tests\Anvilboard.Agent.Tests.csproj
dotnet test Anvilboard.slnx   # full regression
```

Baseline: 521 passing .NET tests and 44 passing Angular tests. No Angular change is in scope, so the
delivery should preserve the 44-test frontend result.

## 13. Milestones & Task Breakdown

All tasks T1–T9 below are **done**; this plan is fully delivered.

| # | Task | Files | Depends on | Status |
|---|---|---|---|---|
| **T1** | `IAuditQueryService` + `AuditQuery` + `AuditEventDto` + `AuditQueryResult` + `AuditQueryException` | `Application/Auditing/IAuditQueryService.cs` | — | Done |
| **T2** | `AuditQueryService` — validation, cursor codec, workspace-first filtering, `ScanCeiling` guard, in-memory ordering with `Id` tiebreak applied **before** `Skip`/`Take` | `Application/Auditing/AuditQueryService.cs` | T1 | Done |
| **T3** | DI registration | `Application/ServiceCollectionExtensions.cs` | T2 | Done |
| **T4** | `AuditEndpoints` + `MapAuditEndpoints()` wiring | `Api/Endpoints/AuditEndpoints.cs`, `Api/Program.cs` | T3 | Done |
| **T5** | Agent operation `list-audit-events`; add to `OperationsWithoutIdempotencyKey` | `Agent/BoardAgentService.cs`, `Agent.Tests/AgentCatalogInvariantsTests.cs` | T3 | Done |
| **T6** | Service tests `AC-AUD-001`…`AC-AUD-011`, `AC-AUD-014`, `AC-AUD-015`, `AC-AUD-016` | `Application.Tests/Audit/AuditQueryServiceTests.cs` | T2 | Done |
| **T7** | API tests `AC-AUD-012` | `Api.Tests/AuditEndpointTests.cs` | T4 | Done |
| **T8** | Agent tests `AC-AUD-013` | `Agent.Tests/AuditOperationTests.cs` | T5 | Done |
| **T9** | Documentation updates | see §13.1 | T6, T7, T8 | Done |

### 13.1 Canonical documents to update on completion

| Document | Change |
|---|---|
| [`docs/features/audit-and-recovery.md`](../features/audit-and-recovery.md) | `Status` row `Partial` → `Implemented`; re-stamp `Last verified` with the delivery commit and new test totals; point `Implementation Plan` at this document; correct the **Interfaces → Outputs** and **Scope → Included** entries from `IAuditService.QueryAsync` to `IAuditQueryService.QueryAsync` per `DR-AUD-001`. |
| [`docs/audit-report.md`](../audit-report.md) | Mark **MAJ-018** RESOLVED with a resolution note and a `Plan:` link here; update the Findings Summary counts from "Open (5)" to "Open (4)". |
| [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) | §16 **M3** row `Partial` → `Implemented`; add `GET /api/audit-events` to the §9.1 endpoint table. |
| [`docs/anvilboard/srs.md`](../anvilboard/srs.md) | Traceability matrix: attach the implementing components to `FR-OPS-001` AC3. |
| [`CHANGELOG.md`](../../CHANGELOG.md) | Entry under the unreleased heading. |

This plan's own `Status` row (§1) flips to **Implemented** in the same change set.

### 13.2 Deliberate follow-ups (not this plan)

| # | Follow-up | Why deferred |
|---|---|---|
| F1 | Angular audit-log view in `anvilboard-web` | UI is owned by a different component and explicitly excluded by the feature spec's Scope. The REST contract this plan delivers is its prerequisite. |
| F2 | Actor display-name resolution | `ActorId` is an opaque, non-member-safe string; resolving it is a presentation concern that belongs with F1. |
| F3 | Audit export | Not required by `FR-OPS-001`; needs its own requirement. |
| F4 | MIN-010 global enum-casing sweep | This plan narrows it (`DR-AUD-004`) but the repo-wide reconciliation is separate. |
| F5 | Retention/pruning | Would introduce the first delete path against an append-only table; needs a requirement and an explicit `FR-OPS-001 AC2` carve-out. |
| F6 | Indexed UTC-ticks column on `AuditEvent` to make ordering database-side, removing the `ScanCeiling` (`DR-AUD-006`) | A schema change plus a backfill migration over the product's largest table; disproportionate to this read-only slice and only worth doing against measured growth. |

## 14. Open Questions & Decision Records

| ID | Decision |
|---|---|
| `DR-AUD-001` | **Read lives on a new `IAuditQueryService`, not on `IAuditService`.** The feature spec names `IAuditService.QueryAsync`, but `IAuditService` has a `NoOpAuditService` implementation that discards writes; a query method on it would have to lie ("no events") or throw. The spec is corrected in §13.1 rather than the code being bent to match it. |
| `DR-AUD-002` | **No new index, no migration.** The existing `{WorkspaceId, OccurredAt}` index covers the mandatory and hot-path predicates. Extra indexes tax every mutation in a single-file SQLite deployment to accelerate an occasional administrative read. Revisit on measured evidence. |
| `DR-AUD-003` | **Offset cursor plus a `ScanCeiling` of 5,000, not keyset.** Keyset paging would need a translatable `ORDER BY` over `DateTimeOffset`, which the SQLite provider cannot produce (§7.4), forcing in-memory ordering and therefore materialization. Unlike the per-issue activity feed this plan otherwise mirrors, `AuditEvents` is workspace-wide and unbounded, so the ceiling is added to keep the memory cost bounded — and it **fails loudly** rather than truncating, because a silently partial audit answer is worse than an error (§8.3). |
| `DR-AUD-004` | **`Channel` is exposed as a live `AuditChannel` enum.** The API already registers `UpperSnakeCaseEnumJsonConverterFactory` (`Anvilboard.Domain/Serialization/UpperSnakeCaseEnumJsonConverter.cs`, wired in `Api/Program.cs`), so it serializes as `"REST"` — identical to the UPPERCASE form the EF value converter persists. Exposing a `.ToString()`-flattened string instead would have added another inconsistent-casing site to MIN-010; this choice narrows the finding rather than widening it. |
| `DR-AUD-005` | **No actor display-name join.** Unlike the activity feed, an audit trail should return the attributed identity as recorded. `ActorId` is opaque and not always a member reference, so resolution is deferred to F2. |
| `OQ-AUD-001` | Should `list-audit-events` be exposed over MCP as well as CLI, or CLI-only? Current assumption: both, since the catalog exposes every operation uniformly and the operation is a permission-gated read. Revisit only if a reason emerges to treat MCP reads as more sensitive. |
| `DR-AUD-006` | **The `ScanCeiling` is a mitigation, not the cure.** The root cause is that `OccurredAt` is a `DateTimeOffset`, which SQLite cannot sort. Adding a persisted, indexed UTC-ticks companion column would let ordering *and* paging run in the database, removing both the materialization and the ceiling. That is a schema change touching the write path and every existing row, so it is deliberately out of scope here and tracked as F6. |
| `OQ-AUD-002` | Should `action` support prefix matching (e.g. `issue.*`)? Deferred — exact match satisfies `FR-OPS-001`, and a prefix surface is easy to add later without breaking the contract. |
