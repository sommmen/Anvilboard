# Implementation Plan: Issue Artifacts

> Feature-level technical design and execution plan for the one remaining **Critical**
> unimplemented capability in Anvilboard.
> Generated with Spec-Forge `tech-design-generation` against the canonical chain:
> [`prd.md`](../anvilboard/prd.md) → [`srs.md`](../anvilboard/srs.md) →
> [`tech-design.md`](../anvilboard/tech-design.md) → [`artifacts.md`](../features/artifacts.md).

## 1. Document Information

| Field | Value |
|---|---|
| Document | Implementation Plan — Issue Artifacts |
| Feature component | [`artifacts`](../features/artifacts.md) |
| Milestone | [`tech-design.md`](../anvilboard/tech-design.md) §16 **M6.7: GitHub PR artifacts** (and the artifact half of **M4.5**) |
| Audit finding | [`audit-report.md`](../audit-report.md) **CRIT-003** (and **MIN-006**, which becomes moot on completion) |
| SRS refs | `FR-ART-001` (primary), `FR-ART-002` (primary), `FR-INT-004` + `FR-OPS-001` + `NFR-SEC-001` (touched) |
| Acceptance criteria | `AC-ART-101` – `AC-ART-111` (from [`artifacts.md`](../features/artifacts.md)) |
| Status | Delivered — T1–T14 complete; see §16 for the per-task breakdown |
| Created | 2026-09-11 |

## 2. Why this feature was selected

Every entry in [`docs/features/overview.md`](../features/overview.md) is `Partial` except
`realtime-updates.md` (`Implemented`) and the backup/restore half of `audit-and-recovery.md`
(delivered under [`backup-and-restore.md`](./backup-and-restore.md)). Selecting "the next feature"
therefore again means selecting the largest verified gap. The evidence:

| Signal | Finding |
|---|---|
| Open GitHub issues | `gh issue list --state open` → none. `gh pr list --state open` → none. The backlog still lives in the feature index + audit report. |
| Unresolved Critical findings | **CRIT-003 only.** CRIT-001 (backup/restore) and CRIT-002 (realtime) are both marked RESOLVED. |
| Code evidence | `Anvilboard.Application/Artifacts/` contains exactly one file — `ArtifactException.cs`. No `IArtifactService.cs`, no `ArtifactService.cs`, no `ArtifactEndpoints.cs`, and no `BoardAgentService` artifact operations. |
| Store reachability | `SqliteArtifactStore` is not registered in any `ServiceCollectionExtensions`; the store is currently unreachable at runtime through DI. |
| Foundations already done | `Domain/Artifact.cs` (+ `ArtifactKind`, `ArtifactKindConverter`), `ArtifactConfiguration`/`ArtifactBlobConfiguration`, migration `20260909080213_AddArtifacts`, `DbSet<Artifact>`/`DbSet<ArtifactBlob>`, `ActivityEventType.ArtifactAttached/ArtifactRefreshed/ArtifactRemoved`, and `ArtifactException` all exist. |
| Milestone status | M6.7 is `Partial`; the artifact half of M4.5 has no service. |

### 2.1 Correction to CRIT-003's framing

[`backup-and-restore.md`](./backup-and-restore.md) §2 characterised CRIT-003 as "a refactor, not a
missing capability". **That characterisation is wrong and this plan supersedes it.** A direct file
inventory of `src/Anvilboard.Application/Artifacts/` shows only `ArtifactException.cs`: there is no
attach, no list, no remove, no refresh, no REST route, no agent operation, and no audit emission.
The capability described by `FR-ART-001` is entirely absent from the running system — an artifact
row can only be created by hand-writing EF code. CRIT-003 is a **missing vertical slice** sitting on
top of finished domain + persistence foundations, which is exactly why it is the right next unit of
work: high requirement coverage, low architectural risk.

## 3. Overview

### 3.1 Background

`FR-ART-001` requires that an authorized actor or automation can attach a file/link/deployment
artifact to an issue, list an issue's artifacts, and remove one — consistently across web, REST,
CLI, and MCP — with content held behind a swappable persistence abstraction and with removal
audited. `FR-ART-002` additionally requires that an approved post-commit lifecycle hook can expand
an external resource (e.g. a Slack thread) into an artifact with *automated* provenance that is
distinguishable from a manual attachment, subject to the same budget, authorization, and audit path.

Anvilboard already has the bottom and top of this stack:

- **Bottom:** `IArtifactStore` / `SqliteArtifactStore` store opaque BLOB references; `Artifact` and
  `ArtifactKind` model the row; EF configuration enforces the `(IssueId, DedupKey)` unique index
  that refresh-in-place depends on.
- **Top:** `IssueEndpoints` already hosts two issue sub-resources (`/comments`, `/links`) whose
  shape this feature copies exactly; `BoardAgentService` already exposes issue sub-resources as
  `[AgentOperation]`s; `AuditService` already records scrubbed audit events.

What is missing is the middle: the single application service that owns validation, workspace
scoping, provenance defaulting, upsert-on-dedup-key, activity/audit emission, and fail-closed store
error translation.

### 3.2 Goals

| # | Goal | Traces to |
|---|---|---|
| G1 | One application service (`ArtifactService`) is the sole caller of `IArtifactStore`, making the interface's existing "sole caller" doc comment true. | CRIT-003, MIN-006 |
| G2 | Attach / list / remove are reachable identically from REST, CLI, and MCP. | `FR-ART-001` AC2, `AC-ART-103` |
| G3 | Refresh-in-place upsert keyed on `(IssueId, Kind, DedupKey)` keeps a `pull_request` artifact in sync with GitHub without creating duplicates. | `AC-ART-110`, `AC-ART-111`, M6.7 |
| G4 | Every artifact mutation emits both an `ActivityEvent` (feed) and an `AuditEvent` (compliance); no silent artifact operation exists. | `FR-ART-001` AC4, `AC-ART-105` |
| G5 | Automation-attached artifacts carry distinguishable provenance (`AddedById = NULL`, `Source = <hook key>`) with no privileged bypass. | `FR-ART-002` AC1/AC2, `AC-ART-106` |
| G6 | A store failure never leaves a partial or orphaned row or blob. | `FR-ART-001` AC3, `AC-ART-104` |

### 3.3 Non-Goals

| # | Non-goal | Why |
|---|---|---|
| N1 | A filesystem- or object-storage-backed `IArtifactStore`. | The contract already supports it; a second implementation is a later, independent change. `SqliteArtifactStore` stays the only implementation. |
| N2 | Artifact content **download** endpoints (`GET .../artifacts/{id}/content`). | `FR-ART-001` names attach/list/remove only. Streaming, range requests, and content-type sniffing are a separate slice with their own security surface. |
| N3 | Angular UI for the artifact panel. | The board UI has no artifact affordance today; adding one is board-service work gated on this API existing. Out of scope, tracked as a follow-up. |
| N4 | A public REST/CLI/MCP route for `RefreshArtifactAsync`. | The feature spec explicitly forbids it — refresh is plugin-only so PR status always reflects what GitHub reports. |
| N5 | Actually shipping the Slack expansion hook or the GitHub PR webhook correlation logic. | Those belong to `integration-and-plugin-platform`. This plan delivers the **seam** they call plus its tests; `GitHubPullRequestArtifactSync` itself is M6.7's second half. |
| N6 | Introducing `/api/v1/` routing. | See OQ-P1 below; matches the precedent already set by the backup plan. |
| N7 | Artifact retention/archive policy beyond hard delete. | `SqliteArtifactStore.DeleteAsync` already purges the blob; that becomes the documented policy. A tiered/archival policy needs a PRD decision. |

### 3.4 Scope

**In scope:** `IArtifactService` + `ArtifactService`; DTO/contract types; DI registration of the
service *and* of `SqliteArtifactStore`; `ArtifactEndpoints` (3 routes); `BoardAgentService`
operations (3); activity + audit emission; error translation; tests for `AC-ART-101`–`AC-ART-111`;
doc updates.

**Out of scope:** everything in §3.3.

### 3.5 User Scenarios

- **US-A1 — Attach evidence.** A contributor triaging a bug pastes a link to a failing CI run onto
  the issue. It appears in the issue's artifact list with their name against it, and in the activity
  feed.
- **US-A2 — Agent attaches its output.** A coding agent finishes a task via MCP and attaches its
  diff/log as a `file` artifact. The board shows it as automation-sourced, not as a human action.
- **US-A3 — PR stays live.** A PR is opened referencing `ANV-42`. A `pull_request` artifact appears.
  When checks finish, the *same* row updates in place — no duplicate rows accumulate over a PR's
  lifetime.
- **US-A4 — Mistaken attachment.** A coordinator removes an artifact attached to the wrong issue.
  The row and blob are gone, and an `ArtifactRemoved` audit event records who removed what and when.
- **US-A5 — Storage outage.** The artifact store is unavailable. The attach attempt returns a clear
  502 `ARTIFACT_STORE_UNAVAILABLE`; the issue is otherwise untouched and no ghost artifact appears.

### 3.6 Acceptance Criteria

All IDs are taken verbatim from [`artifacts.md`](../features/artifacts.md) and are **not**
renumbered.

| AC-ID | Priority | Covered by |
|---|---|---|
| `AC-ART-101` | P1 | T7, T12 — attach persists with actor provenance + audit |
| `AC-ART-102` | P1 | T7 — unrecognized `kind` → `VALIDATION_FAILED`, no row |
| `AC-ART-103` | P1 | T12, T13 — list ordering identical across REST / CLI / MCP |
| `AC-ART-104` | P1 | T7 — store failure → `ARTIFACT_STORE_UNAVAILABLE`, no partial row |
| `AC-ART-105` | P1 | T7, T12 — remove emits `ArtifactRemoved` |
| `AC-ART-106` | P2 | T9 — hook-attached artifact has `Source = "slack-thread-expansion"`, `AddedById = NULL` |
| `AC-ART-107` | P2 | T9 — failed expansion attaches nothing |
| `AC-ART-108` | P2 | T9 — repeat expansion updates in place |
| `AC-ART-109` | P2 | T7 — `artifactId` from another issue → `REFERENCED_ENTITY_NOT_FOUND` |
| `AC-ART-110` | P1 | T8 — first PR event attaches new artifact |
| `AC-ART-111` | P1 | T8 — subsequent PR event refreshes in place, emits `ArtifactRefreshed` |

### 3.7 Success Metrics

| Metric | Target |
|---|---|
| `IArtifactStore` call sites outside `ArtifactService` | 0 (makes the interface doc comment true — MIN-006) |
| `AC-ART-*` criteria with an executing test | 11 / 11 |
| Duplicate `pull_request` rows after *n* webhook events for one PR | 1 row, for any *n* |
| Artifact rows whose blob reference dangles after a failed attach | 0 |

## 4. System Context

```
 REST (/api/issues/{id}/artifacts)      CLI / MCP (BoardAgentService)
                 │                                    │
                 └──────────────┬─────────────────────┘
                                ▼
                    ┌───────────────────────┐
   ILifecycleHook ─►│    ArtifactService    │◄── GitHubPullRequestArtifactSync
   (PostAddComment) │  (Anvilboard.Application/Artifacts)  │    (refresh only, plugin-owned)
                    └───────────┬───────────┘
                   ┌────────────┼─────────────┬───────────────┐
                   ▼            ▼             ▼               ▼
          AnvilboardDbContext  IArtifactStore  IAuditService  ActivityEvents
            (Artifacts)       (SqliteArtifactStore →          (feed + realtime
                               ArtifactBlobs)                  via IssueService
                                                               conventions)
```

`ArtifactService` is the only component that touches `IArtifactStore`. Every surface above it is a
thin adapter that maps transport concerns (HTTP status, agent parameter binding) onto the same
method set, which is what makes `AC-ART-103`'s cross-surface consistency structural rather than
something each surface has to re-implement.

## 5. Solution Design

### 5.1 Solution A (Recommended) — One service, reference-only public API, plugin-only refresh

`ArtifactService` accepts an already-resolved `contentReference` on the public attach path, and
exposes a separate internal overload that accepts raw bytes and calls `IArtifactStore.StoreAsync`
first. `RefreshArtifactAsync` is on the interface but has no transport binding.

- Public REST/CLI/MCP callers attach `link`/`deployment`/`pull_request` artifacts (which are URLs)
  and `file` artifacts whose content the caller already placed in the store.
- The raw-bytes overload is what a hook or a future upload endpoint calls.
- Refresh is reachable only in-process.

**Pros:** smallest possible transport surface; no multipart/form-data handling in this slice; the
store-failure path (`AC-ART-104`) is exercised by exactly one code path; refresh can never be abused
to fake a PR state.
**Cons:** a REST client cannot yet upload a file in one call — it needs the (out-of-scope) content
endpoint. `FR-ART-001` does not require upload, so this is a scope boundary, not a gap.

### 5.2 Solution B (Alternative) — Upload-first: attach accepts a base64/multipart body

Attach always takes content and always calls `StoreAsync`.

**Pros:** one call to attach a file from any surface; feels complete.
**Cons:** drags request-size limits, content-type validation, multipart parsing, and MCP
base64-payload ergonomics into a slice whose requirement doesn't ask for them; every `link` attach
(the common case) pays for a content path it doesn't use; materially widens the security surface
(`NFR-SEC-001`) with no requirement backing.

### 5.3 Comparison Matrix

| Criterion | A — Reference-only + internal bytes overload | B — Upload-first |
|---|---|---|
| `FR-ART-001` coverage | Full | Full |
| New transport surface | 3 simple JSON routes | 3 routes + multipart + size limits |
| Security surface added | Minimal | Upload validation, DoS bounds, content sniffing |
| Store-failure paths to test | 1 | ≥ 2 (upload + hook) |
| Blocks a future upload endpoint? | No — overload is already there | n/a |
| Fits M6.7's remaining allowance | Yes | No |

### 5.4 Decision & Rationale

**Solution A.** It satisfies every acceptance criterion in `artifacts.md` while keeping the new
attack surface to three JSON routes. The bytes-accepting overload means the deferred upload endpoint
is a transport addition later, not a service redesign — the fail-closed store logic lands now and is
tested now (`AC-ART-104`), it just isn't publicly reachable until upload ships.

## 6. Architecture Design

`ArtifactService` follows `IssueLinkService` almost exactly, because artifacts are the third issue
sub-resource and divergence between sub-resources is itself a defect:

1. **Workspace scoping by join, not by trust.** Every method resolves the issue's workspace via
   `db.Issues → db.Teams` and validates from that, mirroring `IssueLinkService`. The caller never
   supplies a workspace ID.
2. **Validate → mutate → record → single `SaveChangesAsync`.** The `Artifact` row and its
   `ActivityEvent` commit in one transaction, so the feed can never disagree with the table.
3. **Store before database, always.** `StoreAsync` runs *before* any row is added. If it throws,
   nothing was tracked, so nothing can be partially committed (`AC-ART-104`). On the remove path the
   order inverts: the row is deleted and committed first, then the blob is purged — an orphaned blob
   is recoverable, an orphaned row is not.
4. **Audit after commit.** `IAuditService.RecordAsync` is called once the mutation is durable,
   matching how `BackupService` sequences audit-after-outcome.
5. **Upsert by unique index, not by read-then-write.** The `(IssueId, DedupKey)` unique index from
   `ArtifactConfiguration` is the concurrency guard for refresh; a losing racer retries the lookup
   once rather than inserting a duplicate.

## 7. Technology Stack & Conventions

### 7.1 Technology Stack Decision

No new dependencies. EF Core + SQLite, ASP.NET Core minimal APIs, `DotNetAgentSurface.Core`'s
`[AgentOperation]`, xUnit — all already in the solution. `System.Text.Json` handles `Metadata`
passthrough, and it is never deserialized by this component.

### 7.2 Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Service | `I{Feature}Service` + sealed impl, primary constructor | `ArtifactService(AnvilboardDbContext db, IArtifactStore store, IAuditService audit, CorrelationContext correlation)` |
| DTO | `record` with static factory | `ArtifactDto.FromArtifact(artifact)` |
| Exception | Existing `ArtifactException(errorCode, message)` | — |
| Wire `kind` | lower-snake via `ArtifactKindConverter` | `pull_request` |
| Agent operation | kebab-case verb-noun | `attach-artifact`, `list-artifacts`, `remove-artifact` |
| Audit action | `PascalCase` matching the activity type | `ArtifactAttached` |

### 7.3 Parameter Validation & Input Parsing

| Parameter | Rule | Failure |
|---|---|---|
| `issueId` | Must exist and resolve to a workspace via its team | `REFERENCED_ENTITY_NOT_FOUND` |
| `kind` | Parsed by `ArtifactKindConverter.TryParse`; closed set | `VALIDATION_FAILED` naming the value |
| `title` | Required, trimmed, 1–500 chars (matches `HasMaxLength(500)`) | `VALIDATION_FAILED` |
| `contentReference` | Required, trimmed, non-empty | `VALIDATION_FAILED` |
| `source` | Optional; trimmed, ≤ 100 chars; defaults to `"local"` when an `actorId` is present, otherwise required | `VALIDATION_FAILED` |
| `dedupKey` | Required on refresh; ≤ 500 chars | `VALIDATION_FAILED` |
| `metadata` | Optional opaque JSON string; size-bounded only (8 KiB), never parsed | `VALIDATION_FAILED` when over bound |
| `artifactId` | Must exist **and** belong to the given `issueId` | `REFERENCED_ENTITY_NOT_FOUND` |

Trimming happens once, in the service — not in each endpoint — so CLI and REST cannot diverge.

### 7.4 Boundary Values & Edge Cases

| Case | Behavior |
|---|---|
| `title` of exactly 500 chars / 501 chars | Accepted / `VALIDATION_FAILED` (never a truncating DB error) |
| Empty artifact list | `[]`, HTTP 200 — not 404 |
| Two artifacts with identical `CreatedAt` | Tie-broken by `Id` so ordering is deterministic across surfaces (`AC-ART-103`) |
| `dedupKey = null` on attach | Allowed and non-unique — the unique index is filtered, so many `link` artifacts coexist |
| Refresh with a `kind` other than `pull_request` | `VALIDATION_FAILED` |
| Refresh for an issue that already has the row, with unchanged values | Still updates `UpdatedAt` and still emits `ArtifactRefreshed`; idempotent, not a no-op |
| Remove of an already-removed artifact | `REFERENCED_ENTITY_NOT_FOUND` (not silent success) |
| Blob purge fails after the row is committed | Remove still succeeds; the failure is logged as a diagnostic — the user-visible removal already happened |

### 7.5 Business Logic Rules

| ID | Rule |
|---|---|
| BR-ART-1 | `Source` defaults to `"local"` only when `actorId` is non-null. An automation caller **must** pass an explicit source key; there is no anonymous `"local"` automation. |
| BR-ART-2 | `AddedById = NULL` ⟺ automation-attached. This is the sole provenance discriminator (`AC-ART-106`). |
| BR-ART-3 | `RefreshArtifactAsync` is valid only for `ArtifactKind.PullRequest`. |
| BR-ART-4 | Refresh that finds no existing `(IssueId, Kind, DedupKey)` row inserts and emits `ArtifactAttached`; one that finds a row updates and emits `ArtifactRefreshed` (`AC-ART-110` / `AC-ART-111`). |
| BR-ART-5 | Artifact authorization is exactly the parent issue's mutation authorization. No separate artifact role exists. |
| BR-ART-6 | `Metadata` is written only for refreshable kinds and is never interpreted by this component. |
| BR-ART-7 | Removal hard-deletes the blob via `IArtifactStore.DeleteAsync`; that is `SqliteArtifactStore`'s documented retention policy, satisfying "never silently purged outside the documented policy". |

### 7.6 Error Handling Strategy

Every anticipated failure is an `ArtifactException(errorCode, message)`; no `SqliteException`,
`DbUpdateException`, or store I/O exception escapes the service. Endpoints map `ErrorCode` → HTTP
with the same `switch` shape `IssueEndpoints` already uses for `IssueLinkException`. The agent
surface lets `ArtifactException` propagate — `ErrorCatalogTranslator` already turns catalog codes
into agent-shaped errors.

### 7.7 Error Catalog & Traceability

| Condition | Code | HTTP | Catalog source |
|---|---|---|---|
| Issue missing / outside workspace; artifact not on that issue | `REFERENCED_ENTITY_NOT_FOUND` | 404 | tech-design §7.7 |
| Unrecognized `kind` | `VALIDATION_FAILED` | 400 | §7.7 |
| Refresh on a non-`pull_request` kind | `VALIDATION_FAILED` | 400 | §7.7 |
| Missing/over-long `title` or `contentReference` | `VALIDATION_FAILED` | 400 | §7.7 |
| `IArtifactStore` read/write failure | `ARTIFACT_STORE_UNAVAILABLE` | 502 | §7.7 — **this plan introduces its first producer** |
| Actor lacks workspace permission | `WORKSPACE_ACCESS_DENIED` | 403 | §7.7, OQ-001 uniform-403 decision |

## 8. Detailed Design

### 8.1 Component Overview

| Component | Project | Responsibility |
|---|---|---|
| `IArtifactService` / `ArtifactService` | `Anvilboard.Application/Artifacts/` | Validation, workspace scoping, provenance, upsert, activity + audit emission, store-error translation |
| `ArtifactDto` | `Anvilboard.Application/Artifacts/` | REST/agent response shape |
| `ArtifactException` | `Anvilboard.Application/Artifacts/` | **Exists** — reused unchanged |
| `ArtifactEndpoints` | `Anvilboard.Api/Endpoints/` | `GET` / `POST` / `DELETE` under `/api/issues/{id}/artifacts` |
| `BoardAgentService` additions | `Anvilboard.Agent/` | `attach-artifact`, `list-artifacts`, `remove-artifact` |
| `SqliteArtifactStore` DI registration | `Anvilboard.Infrastructure/` | **New registration** of an existing type |

### 8.2 Contracts

```csharp
namespace Anvilboard.Application.Artifacts;

/// <summary>
/// The single write/read path for issue artifacts, used identically by the REST endpoints, the
/// CLI/MCP agent surface, and in-process lifecycle hooks — and the only caller of
/// <see cref="IArtifactStore"/> (docs/features/artifacts.md, "Persistence abstraction ownership").
/// </summary>
public interface IArtifactService
{
    /// <summary>Attaches an artifact whose content is already addressable by
    /// <paramref name="contentReference"/>. <paramref name="source"/> defaults to "local" only when
    /// <paramref name="actorId"/> is supplied; automation callers must pass their own hook key.</summary>
    Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        string contentReference,
        string? source = null,
        MemberId? actorId = null,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>Stores <paramref name="content"/> through <see cref="IArtifactStore"/> and attaches
    /// the resulting reference atomically: if the store throws, no row is created
    /// (AC-ART-104).</summary>
    Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        byte[] content,
        string? contentType,
        string source,
        MemberId? actorId = null,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>All artifacts on the issue, CreatedAt ascending, Id as tie-break (AC-ART-103).</summary>
    Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(IssueId issueId, CancellationToken ct = default);

    /// <summary>Idempotent upsert on (issueId, kind, dedupKey). Valid only for "pull_request".
    /// Not bound to any transport — plugin correlation logic only (BR-ART-3).</summary>
    Task<ArtifactDto> RefreshArtifactAsync(
        IssueId issueId,
        string kind,
        string dedupKey,
        string title,
        string contentReference,
        string source,
        string? metadata = null,
        CancellationToken ct = default);

    /// <summary>Removes the artifact and purges its content per the active store's documented
    /// policy; always emits ArtifactRemoved (AC-ART-105).</summary>
    Task RemoveArtifactAsync(
        IssueId issueId,
        ArtifactId artifactId,
        MemberId? actorId = null,
        CancellationToken ct = default);
}

public sealed record ArtifactDto(
    Guid Id,
    Guid IssueId,
    string Kind,
    string Title,
    string ContentReference,
    string Source,
    Guid? AddedById,
    string? DedupKey,
    string? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ArtifactDto FromArtifact(Artifact artifact) => new(
        artifact.Id.Value,
        artifact.IssueId.Value,
        ArtifactKindConverter.ToWireValue(artifact.Kind),
        artifact.Title,
        artifact.ContentReference,
        artifact.Source,
        artifact.AddedById?.Value,
        artifact.DedupKey,
        artifact.Metadata,
        artifact.CreatedAt,
        artifact.UpdatedAt);
}
```

### 8.3 Core Workflow — `AttachArtifactAsync` (bytes overload)

```
1. Resolve workspace:  Issues ⨝ Teams  →  (issue, workspaceId)
      miss → REFERENCED_ENTITY_NOT_FOUND            [AC-ART-109 sibling]
2. Validate kind via ArtifactKindConverter.TryParse
      miss → VALIDATION_FAILED  (no store call, no row)   [AC-ART-102]
3. Validate title / contentReference / source / metadata bounds
      miss → VALIDATION_FAILED
4. contentReference = await store.StoreAsync(content, contentType, ct)
      throws → wrap as ARTIFACT_STORE_UNAVAILABLE and return
               (nothing tracked ⇒ nothing to roll back)    [AC-ART-104]
5. db.Artifacts.Add(row)  +  db.ActivityEvents.Add(ArtifactAttached)
6. await db.SaveChangesAsync(ct)          ← single transaction
      throws → best-effort store.DeleteAsync(reference); rethrow as
               ARTIFACT_STORE_UNAVAILABLE-free VALIDATION/translated error
7. await audit.RecordAsync(ArtifactAttached, target = artifactId, ct)
      summary = { issueId, artifactId, kind, source } — never content  [AC-ART-101]
8. return ArtifactDto.FromArtifact(row)
```

Step 4 preceding step 5 is the whole of `AC-ART-104`: the store call happens while the change
tracker is still empty, so the failure path has literally nothing to undo. Step 6's compensating
delete covers the narrower window where the blob landed but the row did not.

The reference-only overload skips steps 4 and 6's compensation entirely.

### 8.4 Core Workflow — `RefreshArtifactAsync` (idempotent upsert)

```
1. Resolve workspace as above → REFERENCED_ENTITY_NOT_FOUND on miss
2. kind must parse AND equal ArtifactKind.PullRequest
      else → VALIDATION_FAILED                              [BR-ART-3]
3. Validate dedupKey / title / contentReference / metadata bound
4. existing = Artifacts.FirstOrDefault(IssueId == id
                                    && Kind == PullRequest
                                    && DedupKey == dedupKey)
5a. existing is null →
        insert row (DedupKey set), ActivityEventType.ArtifactAttached,
        audit action "ArtifactAttached"                      [AC-ART-110]
5b. existing is not null →
        update ContentReference / Title / Metadata / UpdatedAt in place,
        ActivityEventType.ArtifactRefreshed,
        audit action "ArtifactRefreshed"                     [AC-ART-111]
6. SaveChangesAsync
      DbUpdateException on the (IssueId, DedupKey) unique index
        → re-read once and apply 5b (lost an insert race), then save
7. audit.RecordAsync(...) after commit
8. return DTO
```

Step 6 is why the unique index is the concurrency control rather than a read-modify-write lock: two
concurrent webhook deliveries for the same PR converge on one row without a transaction escalation.

### 8.5 Core Workflow — `RemoveArtifactAsync`

```
1. artifact = Artifacts.FirstOrDefault(Id == artifactId && IssueId == issueId)
      miss → REFERENCED_ENTITY_NOT_FOUND
             (covers both "gone" and "belongs to another issue")   [AC-ART-109]
2. reference = artifact.ContentReference
3. db.Artifacts.Remove(artifact) + ActivityEvent(ArtifactRemoved)
4. SaveChangesAsync                        ← removal is durable here
5. audit.RecordAsync(ArtifactRemoved, ...)                          [AC-ART-105]
6. best-effort store.DeleteAsync(reference)
      failure → log diagnostic only; the removal already succeeded  [BR-ART-7]
```

Order is deliberately inverted relative to attach: a dangling blob is a reclaimable-space problem, a
dangling row is a correctness problem the user can see.

## 9. API Design

### 9.1 API Overview

| Method | Route | Permission | Notes |
|---|---|---|---|
| `GET` | `/api/issues/{id:guid}/artifacts` | `ReadBoard` (group-level) | `CreatedAt` ascending |
| `POST` | `/api/issues/{id:guid}/artifacts` | `ReadWriteIssues` \| `ReadWriteAssignedIssues` | JSON body, reference-only |
| `DELETE` | `/api/issues/{id:guid}/artifacts/{artifactId:guid}` | `ReadWriteIssues` \| `ReadWriteAssignedIssues` | `204 No Content` |

Permissions mirror `/links` exactly (BR-ART-5). `RefreshArtifactAsync` intentionally has **no**
route (N4).

### 9.2 Detailed Specifications

```http
POST /api/issues/{id}/artifacts
Content-Type: application/json

{ "kind": "link",
  "title": "Failing CI run",
  "contentReference": "https://github.com/o/r/actions/runs/123",
  "source": null,
  "actorId": "6f9…",
  "metadata": null }

201 Created
Location: /api/issues/{id}/artifacts/{artifactId}
{ "id": "…", "issueId": "…", "kind": "link", "title": "Failing CI run",
  "contentReference": "https://…", "source": "local", "addedById": "6f9…",
  "dedupKey": null, "metadata": null,
  "createdAt": "2026-09-11T10:00:00+00:00", "updatedAt": "2026-09-11T10:00:00+00:00" }
```

Errors use `Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: …)` with the §7.7
mapping — byte-for-byte the shape `/links` already returns.

### 9.3 Agent / CLI / MCP surface

Added to `BoardAgentService`, wrapping `IArtifactService` the same way the existing issue-link
operations wrap `IssueLinkService`:

| Operation | Signature | Idempotent |
|---|---|---|
| `list-artifacts` | `ListArtifactsAsync(Guid issueId, ct)` | yes |
| `attach-artifact` | `AttachArtifactAsync(Guid issueId, string kind, string title, string contentReference, string? source, ct)` | no |
| `remove-artifact` | `RemoveArtifactAsync(Guid issueId, Guid artifactId, ct)` | no |

The agent host is unauthenticated today (MAJ-001/MAJ-015), so agent-originated attaches pass
`actorId: null` with an explicit `source` — which is precisely the automation provenance BR-ART-1
and BR-ART-2 require, not a workaround. They therefore inherit the existing `agent:automation` actor
convention for their audit rows. Exposing artifact *refresh* on this surface stays refused (N4).

## 10. Data & Storage Design

### 10.1 Schema

**No migration.** `20260909080213_AddArtifacts` already created both tables with the exact shape
this service needs:

| Column | Used by |
|---|---|
| `IssueId` (indexed) | list + all scoping |
| `Kind` (`nvarchar(20)`, wire value) | validation |
| `Title` (`nvarchar(500)`) | §7.3 length bound |
| `ContentReference` (required) | store handle |
| `Source` (`nvarchar(100)`) | provenance |
| `AddedById` (nullable) | provenance discriminator |
| `DedupKey` (`nvarchar(500)`), filtered unique with `IssueId` | refresh upsert + race guard |
| `Metadata` (nullable) | opaque PR payload |
| `CreatedAt` / `UpdatedAt` | ordering + refresh signal |

That the schema already fits without amendment is the strongest evidence that this is a missing
service layer rather than a missing capability design.

### 10.2 Configuration

None. `SqliteArtifactStore` needs no options — it writes to the same `AnvilboardDbContext`. A future
filesystem store introduces its own options then.

## 11. Security Design

### 11.1 / 11.2 Authentication & Authorization

Artifact routes sit inside the existing `/api/issues` group, so they inherit `ReadBoard` plus the
`RequirePermission(ReadWriteIssues, ReadWriteAssignedIssues)` mutation gate. Workspace scoping is
re-derived server-side from the issue's team on every call — a caller cannot widen scope by
supplying an artifact ID from another workspace, because the `(artifactId, issueId)` pair is matched
before anything else happens (`AC-ART-109`).

### 11.3 Data protection

- `Metadata` is stored verbatim and never logged; audit summaries carry `{ issueId, artifactId,
  kind, source }` only, never `contentReference` content or `Metadata` (which could contain a PR
  title from a private repo).
- `SecretRedactor.Scrub` already runs over every audit `ResultSummary`, so the artifact rows get the
  same treatment as backup/restore rows for free.
- `ContentReference` is opaque and never parsed, concatenated into a path, or used to build a URL by
  this component — closing the path-traversal class of bug before a filesystem store exists.

### 11.4 Audit logging

| Action | When | Target | Summary |
|---|---|---|---|
| `ArtifactAttached` | after commit on both attach overloads and on refresh-insert | `artifactId` | `{ issueId, kind, source }` |
| `ArtifactRefreshed` | after commit on refresh-update | `artifactId` | `{ issueId, kind, source, dedupKey }` |
| `ArtifactRemoved` | after commit on remove | `artifactId` | `{ issueId, kind, source }` |

Channel is `Rest`, `Cli`, `Mcp`, or `Automation` depending on the caller, resolved the same way
`BoardAgentService` already resolves it. The matching `ActivityEventType` members already exist in
`ActivityEvent.cs` — no domain change is needed.

## 12. Performance Design

Artifact counts per issue are small (single digits typically, bounded in practice by PR count).
`ListArtifactsAsync` is a single indexed read on `IssueId` with no pagination — consistent with how
`/comments` and `/links` behave today. Refresh adds one indexed lookup per webhook event, which is
negligible against the webhook's own I/O. No caching, no batching, no background work.

## 13. Observability

- Activity feed rows make attach/refresh/remove visible in the UI immediately.
- Audit rows make them queryable for compliance (and will be reachable once `FR-OPS-001` query
  access lands).
- `ILogger<ArtifactService>` warns on exactly two conditions: a blob purge that failed after a
  committed removal, and a unique-index race retried on refresh. Both are benign-but-interesting.
- No new metrics; `RealtimeMetrics` is not extended because artifacts ride the existing issue
  activity stream.

## 14. Deployment & Rollback

Additive only: new service, new routes, new agent operations, one new DI registration. No migration,
no config, no data backfill. Rollback is removing the routes/operations — existing rows remain
readable by any later re-deployment because the schema is untouched.

## 15. Testing Strategy

| Layer | Project | Coverage |
|---|---|---|
| Unit / integration | `Anvilboard.Application.Tests/Artifacts/ArtifactServiceTests.cs` | `AC-ART-101`, `102`, `104`, `105`, `109`; validation bounds; `source` defaulting (BR-ART-1); ordering tie-break; remove-after-remove |
| Integration | `Anvilboard.Application.Tests/Artifacts/ArtifactRefreshTests.cs` | `AC-ART-110`, `AC-ART-111`; unique-index race convergence; refresh on wrong kind |
| Fault injection | same, via a `ThrowingArtifactStore` double | `AC-ART-104` — asserts `ARTIFACT_STORE_UNAVAILABLE` **and** `Artifacts.Count == 0` |
| Hook seam | `Anvilboard.Application.Tests/Artifacts/ArtifactExpansionTests.cs` | `AC-ART-106`, `107`, `108` — automation provenance, no-row-on-failure, repeat-expansion idempotency |
| API | `Anvilboard.Api.Tests/Artifacts/ArtifactEndpointTests.cs` (via `ApiFactory`) | All three routes; error→status mapping; 201 `Location`; permission denial |
| Cross-surface | `Anvilboard.Api.Tests/Artifacts/ArtifactEndpointTests.cs` + agent test | `AC-ART-103` — REST list and `BoardAgentService.ListArtifactsAsync` return identical ordering for the same seeded issue |

Fixtures needed: a seeded workspace → team → issue (reuse `IssueLinkFixture`'s shape from
[`IssueLinkServiceTests.cs`](../../src/Anvilboard.Application.Tests/Issues/IssueLinkServiceTests.cs));
a second issue in the same workspace for `AC-ART-109`; a `ThrowingArtifactStore` double; a fake
expansion hook. [`ApiFactory`](../../src/Anvilboard.Api.Tests/Testing/ApiFactory.cs) needs **no**
changes — its throwaway temp SQLite path already covers blob storage.

## 16. Milestones & Task Breakdown

Dependency-ordered. Sized against the remainder of the §16 M6.7 allowance.

| # | Task | Files | Depends on | Est. |
|---|---|---|---|---|
| T1 | `ArtifactDto` + static factory | `Anvilboard.Application/Artifacts/ArtifactDto.cs` | — | 0.5 h |
| T2 | `IArtifactService` contract (all four methods, XML docs citing the AC IDs) | `Anvilboard.Application/Artifacts/IArtifactService.cs` | T1 | 1 h |
| T3 | `ArtifactService` — workspace resolution, validation helpers, activity recording helper | `Anvilboard.Application/Artifacts/ArtifactService.cs` | T2 | 3 h |
| T4 | `ArtifactService` — both attach overloads incl. store-first ordering and compensating delete | same | T3 | 3 h |
| T5 | `ArtifactService` — `ListArtifactsAsync` + `RemoveArtifactAsync` (inverted order, blob purge) | same | T3 | 2 h |
| T6 | `ArtifactService` — `RefreshArtifactAsync` upsert + unique-index race retry | same | T4 | 3 h |
| T7 | Application tests: attach/list/remove/validation/fault-injection | `Anvilboard.Application.Tests/Artifacts/ArtifactServiceTests.cs` | T5 | 5 h |
| T8 | Refresh tests incl. race convergence | `Anvilboard.Application.Tests/Artifacts/ArtifactRefreshTests.cs` | T6 | 3 h |
| T9 | Hook-seam tests (automation provenance, failure, repeat) | `Anvilboard.Application.Tests/Artifacts/ArtifactExpansionTests.cs` | T6 | 3 h |
| T10 | DI: `IArtifactService` in Application + **`IArtifactStore` → `SqliteArtifactStore`** in Infrastructure | both `ServiceCollectionExtensions.cs` | T6 | 0.5 h |
| T11 | `ArtifactEndpoints` + registration in `Program.cs` | `Anvilboard.Api/Endpoints/ArtifactEndpoints.cs`, `Anvilboard.Api/Program.cs` | T10 | 2 h |
| T12 | API endpoint tests incl. permission denial and error mapping | `Anvilboard.Api.Tests/Artifacts/ArtifactEndpointTests.cs` | T11 | 4 h |
| T13 | `BoardAgentService` operations + cross-surface ordering assertion (`AC-ART-103`) | `Anvilboard.Agent/BoardAgentService.cs`, agent test | T10 | 2.5 h |
| T14 | Doc updates per §16.1 | see below | T13 | 1.5 h |

T10 is small but load-bearing: `SqliteArtifactStore` is currently registered **nowhere**, so the
service cannot resolve without it. It is called out as its own task so it cannot be lost inside T6.

### 16.1 Canonical documents to update on completion

Doc-first discipline: edited **in place**; no parallel or `-v2` files, and all existing
`FR-*`/`AC-*`/finding IDs are preserved.

| Document | Edit |
|---|---|
| [`docs/features/artifacts.md`](../features/artifacts.md) | Status row → Implemented; File Structure loses its `# Planned:` markers and gains the real paths (incl. `/api/issues`, not `/api/v1/issues`) |
| [`docs/features/overview.md`](../features/overview.md) | Row 8 status; note the residual gaps (content download, Angular panel, `GitHubPullRequestArtifactSync`) |
| [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) | §16 M6.7 row; §7.7 `ARTIFACT_STORE_UNAVAILABLE` gains a real producer |
| [`docs/audit-report.md`](../audit-report.md) | CRIT-003 and MIN-006 marked RESOLVED with evidence, in the same style as the existing CRIT-002 block; note the §2.1 correction to CRIT-003's "refactor" framing |
| [`docs/plans/backup-and-restore.md`](./backup-and-restore.md) | §2's "CRIT-003 is a refactor, not a missing capability" line corrected with a pointer to §2.1 here |

## 17. Open Questions & Decision Records

| ID | Question / Decision | Status | Resolution |
|---|---|---|---|
| OQ-P1 | REST routes: `/api/issues/{id}/artifacts` (matching code) or `/api/v1/issues/...` (matching `artifacts.md` File Structure)? | Resolved | Use `/api/issues/{id}/artifacts`. No route in `src/` carries a `/v1` segment; the spec's `/v1` is aspirational. Identical reasoning and outcome as `backup-and-restore.md` OQ-P1. The `artifacts.md` File Structure block is corrected in T14. |
| OQ-P2 | Should attach accept raw content over REST in this slice? | Resolved | No (§5.4 / N2). The bytes overload exists and is tested, but is reachable only in-process until a dedicated upload endpoint is specified. |
| OQ-P3 | Expose `refresh-artifact` on REST/CLI/MCP? | Resolved | No. `artifacts.md` Constraints forbid it explicitly: PR state must only ever reflect what GitHub reports. |
| OQ-P4 | Does artifact mutation need a realtime publication like `IssueService` does? | Deferred | Not in this slice. `RealtimeIssueChangeKind` has no artifact member and the board UI has no artifact panel (N3), so there is nothing to push to. Revisit with the UI work. |
| OQ-P5 | Hard delete or archive on removal? | Resolved | Hard delete via `SqliteArtifactStore.DeleteAsync`, documented as that store's retention policy (BR-ART-7). "Not silently purged outside the documented policy" is satisfied by documenting it, not by retaining. |
| OQ-P6 | Should agent-originated attaches reuse `agent:automation`? | Resolved | Yes — the convention `backup-and-restore.md` §11.4 established. Revisit when MAJ-001/MAJ-015 give the agent surface real actor identity. |
| OQ-P7 | How does `ArtifactService` learn which `AuditChannel` a call arrived on, given it has no in-area precedent? | Resolved during implementation | Threaded explicitly: each mutating `IArtifactService` method takes `AuditChannel channel = AuditChannel.System` before its `CancellationToken`, mirroring `BackupOperationContext`. An ambient accessor was rejected because the same scoped instance serves REST, CLI, MCP, and hooks within one request. `RefreshArtifactAsync` has no channel parameter — being plugin-only, it is always `System`. |
| OQ-P8 | Should `RefreshArtifactAsync` widen beyond `pull_request` to any kind carrying a `DedupKey`? | Open | Not in this slice — BR-ART-3 restricts it to `pull_request`, so a `link`-kind expansion hook must converge by remove-then-attach instead of upserting in place. `ArtifactExpansionTests.RepeatExpansion_UpdatesExistingArtifactIdempotently` therefore asserts row *count*, not row *identity*. Revisit if a second refreshable kind appears. |

## 18. Appendix

### A. Glossary

| Term | Meaning |
|---|---|
| Artifact | A file, link, deployment, or pull-request reference attached to an issue |
| Content reference | Opaque locator returned by `IArtifactStore.StoreAsync`; never parsed by callers |
| Dedup key | Provider identity (e.g. `github:{repo}#{number}`) making a refreshable artifact upsertable |
| Refreshable kind | Currently only `pull_request` — a kind whose row is updated in place from its source of truth |
| Provenance | `AddedById` + `Source`; distinguishes human from automation attachment |

### B. References

- [`docs/features/artifacts.md`](../features/artifacts.md) — feature spec (authoritative ACs)
- [`docs/anvilboard/srs.md`](../anvilboard/srs.md) — `FR-ART-001`, `FR-ART-002`
- [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) — §7.7, §9.1, §10.1, §16
- [`docs/audit-report.md`](../audit-report.md) — CRIT-003, MIN-006

### C. Related Documents

- [`docs/plans/backup-and-restore.md`](./backup-and-restore.md) — the precedent this plan follows,
  and whose §2 CRIT-003 characterisation §2.1 corrects
- [`docs/features/integration-and-plugin-platform.md`](../features/integration-and-plugin-platform.md)
  — owns `GitHubPullRequestArtifactSync`, the caller of `RefreshArtifactAsync`
- [`docs/features/issue-linking.md`](../features/issue-linking.md) — the structural precedent
  (`IssueLinkService`) this service mirrors

### D. Requirements Traceability

| Requirement | Criterion | Design section | Test |
|---|---|---|---|
| `FR-ART-001` AC1 | Kind/title/reference/actor/timestamp recorded | §8.2, §10.1 | `AC-ART-101`, `AC-ART-102` |
| `FR-ART-001` AC2 | Attach/list/remove on web, REST, CLI, MCP | §9.1, §9.3 | `AC-ART-103` |
| `FR-ART-001` AC3 | Single store contract, swappable | §4, §6, G1 | `AC-ART-104` |
| `FR-ART-001` AC4 | Removal audited, no silent purge | §8.5, §11.4, BR-ART-7 | `AC-ART-105`, `AC-ART-109` |
| `FR-ART-002` AC1 | Hook path, same budget/authz/audit | §9.3, §11.4, BR-ART-5 | `AC-ART-106` |
| `FR-ART-002` AC2 | Automated provenance distinguishable | BR-ART-1, BR-ART-2 | `AC-ART-106` |
| `FR-ART-002` AC3 | Failed expansion attaches nothing | §8.3 step 4 | `AC-ART-107`, `AC-ART-108` |
| M6.7 | PR artifact attach + refresh | §8.4 | `AC-ART-110`, `AC-ART-111` |
