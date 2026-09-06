# Issue Artifacts

> Feature spec for Spec-Forge implementation planning.
> Source: extracted from docs/anvilboard/tech-design.md §8.1, §10.1 `Artifacts` table
> Created: 2026-09-05

| Field | Value |
|-------|-------|
| Component | artifacts |
| Priority | P1 |
| SRS Refs | FR-ART-001, FR-ART-002 |
| Tech Design Ref | §8.1 — Issue Artifacts row; also §7.7 Error Catalog, §9.1 API Design, §10.1 `Artifacts` table |
| Depends On | issue-board-service, workspace-authorization |
| Blocks | integration-and-plugin-platform (artifact-expansion hook), agent-and-automation-surface |

## Purpose

Issue Artifacts lets an authorized actor or automation attach a file, link, deployment, or pull-request reference to an issue — a screenshot, a deployment URL, an expanded Slack thread, a linked GitHub PR whose status stays current — without inlining large or binary content into the `Issues` table itself. Every artifact operation flows through the Issue & Board Service's authorization/audit path (never a privileged bypass), and artifact content is stored behind a single persistence abstraction (`IArtifactStore`) so the first-release SQLite BLOB-backed implementation can later be swapped for a filesystem- or object-storage-backed one without changing any caller.

## Scope

**Included:**
- Attaching a file, link, deployment, or pull-request artifact to an issue, recording kind, title, content reference, source (manual vs. automated), attaching actor, and timestamp (FR-ART-001 AC1).
- Listing and removing artifacts on an issue, exposed consistently across web, REST, CLI, and MCP (FR-ART-001 AC2).
- A single persistence-abstraction contract (`IArtifactStore`: store/retrieve/delete by opaque reference) with a SQLite BLOB-backed default implementation (FR-ART-001 AC3).
- Auditing artifact removal; artifact content is not silently purged outside the documented retention/archive policy (FR-ART-001 AC4).
- Automated artifact expansion via an approved `Post*` `ILifecycleHook<TEvent>` (e.g., a linked Slack thread expanded into artifact content), recording distinct automated provenance and honoring the same execution budget, authorization, and audit trail as any other hook-driven write (FR-ART-002).
- A `pull_request`-kind artifact carrying refreshable `Metadata` (PR number, state, checks status) that a plugin upserts in place on subsequent PR events rather than creating a duplicate artifact, so the board/dashboard always shows the PR's current status (FR-ART-001 AC1 realization: pull-request kind with metadata).

**Excluded:**
- Deciding *which* external resources to expand or how to fetch them — that logic (URL matching, provider fetch clients) lives in the Integration & Plugin Platform's artifact-expansion hook (`docs/features/integration-and-plugin-platform.md`); this component only accepts and stores the resulting artifact.
- Deciding *how* a pull request is correlated to an issue (reference parsing in PR title/body/branch name, or an explicit link) — that logic lives in the GitHub plugin (`docs/features/integration-and-plugin-platform.md`); this component only stores and refreshes the resulting `pull_request` artifact.
- Workflow-state legality, issue CRUD, and concurrency control (owned by `issue-board-service`; this component is a sub-resource of an issue, not an independent aggregate).
- Authenticating/authorizing the calling actor (owned by `workspace-authorization`; this component receives an already-authorized request).
- A filesystem- or object-storage-backed `IArtifactStore` implementation — the first release ships the SQLite-backed implementation only; alternate implementations are a future swap of the same interface (tech-design §10.1).
- Rendering/previewing artifact content in the UI (a presentation-layer concern outside this spec's implementation scope; this component only stores and returns the opaque `ContentReference`).

## Core Responsibilities

1. **Artifact CRUD** — attach, list, and remove artifacts scoped to a single issue, validating `Kind` is one of `file`/`link`/`deployment`/`pull_request` and that `Title`/`ContentReference` are present.
2. **Persistence abstraction ownership** — define and own the `IArtifactStore` contract so callers (including this component's own service layer) never depend on a concrete storage mechanism.
3. **Provenance tracking** — distinguish manually-attached artifacts (`AddedById` set, `Source = "local"`) from automation-attached artifacts (`AddedById` NULL, `Source` set to the originating integration/hook key, e.g. `slack-thread-expansion`, `github`).
4. **Audit emission** — emit an activity/audit event for every attach, refresh, and remove, routed through the same audit path as any other issue mutation (never a separate, un-audited path).
5. **Fail-closed storage errors** — surface a store read/write failure as `ARTIFACT_STORE_UNAVAILABLE` rather than a raw I/O exception or a silently-corrupted attachment.
6. **Refreshable-kind upsert semantics** — for kinds that carry a live, externally-owned status (currently only `pull_request`), support an idempotent upsert-by-dedup-key operation that updates the existing artifact's `Metadata`/`ContentReference` in place rather than creating a duplicate row.

## Interfaces

### Inputs
- **`AttachArtifactAsync(issueId, kind, title, contentReference, source?, actorId?, metadata?)`** — via `POST /api/v1/issues/{id}/artifacts` and equivalent CLI/MCP operations; `source`/`actorId` distinguish manual (actor-driven) from automated (hook-driven, `actorId` omitted) attachment; `metadata` is an optional opaque key-value bag used only by refreshable kinds (currently `pull_request`: `{ number, state, checksStatus }`), ignored/absent for the other kinds.
- **`ListArtifactsAsync(issueId)`** — via `GET /api/v1/issues/{id}/artifacts`.
- **`RemoveArtifactAsync(issueId, artifactId, actorId)`** — via `DELETE /api/v1/issues/{id}/artifacts/{artifactId}`.
- **`RefreshArtifactAsync(issueId, kind, dedupKey, contentReference, metadata, source)`** (realization of FR-ART-001 idempotent upsert) — an idempotent upsert used by refreshable kinds: if an artifact matching `(issueId, kind, dedupKey)` exists, its `ContentReference`/`Metadata` are updated in place; otherwise a new artifact is attached. Not exposed as a distinct public endpoint — invoked only by the owning plugin's correlation logic (e.g. the GitHub plugin's `GitHubPullRequestArtifactSync`), never by a human/API/CLI/MCP caller directly.
- **Lifecycle-hook calls** — a `Post*` `ILifecycleHook<TEvent>` implementation (owned by `integration-and-plugin-platform`, e.g. registered for `PostAddComment`/`PostIngest`) calls `AttachArtifactAsync` exactly as any other caller would, with `actorId` omitted and `source` set to its hook key; the GitHub PR-correlation plugin calls `RefreshArtifactAsync` with `source = "github"`.

### Outputs
- **`Artifact` DTO** — `(id, issueId, kind, title, contentReference, source, addedById, createdAt, metadata)`, returned from attach/list/refresh; `contentReference` is the opaque locator, resolved by the active `IArtifactStore` — callers never receive raw bytes from this surface (retrieval of content, if needed, is a separate, store-specific concern not covered by this spec's list/attach/remove contract). `metadata` is `NULL`/empty for `file`/`link`/`deployment` kinds and populated for `pull_request` (FR-ART-001 AC1 realization); the UI renders a PR artifact's live status directly from this field without a separate fetch.
- **Audit/activity events** — `ArtifactAttached`/`ArtifactRefreshed`/`ArtifactRemoved`, consumed by `audit-and-recovery`, carrying actor (or hook key) and artifact kind/title (never raw content); `ArtifactRefreshed` also feeds `issue-board-service`'s activity-history templating (e.g. "PR #234 marked as merged") and `realtime-updates`'s broadcast so a PR status change reflects on the dashboard immediately.

### Dependencies
- **`issue-board-service`** — supplies the issue existence/authorization context; an artifact can only be attached to an issue that exists and that the actor is authorized to mutate.
- **`workspace-authorization`** — supplies the authorized actor context for manual attach/remove operations.
- **`IArtifactStore`** (`Anvilboard.Infrastructure`, SQLite BLOB-backed for this release) — the sole storage mechanism this component's service layer calls; never bypassed by direct `DbContext`/filesystem access.
- **`integration-and-plugin-platform`** — the caller for automated artifact-expansion attachments (FR-ART-002) and GitHub PR-artifact refreshes (FR-ART-001 refresh operation); this component does not depend on it, but is depended upon by it.

## Data Flow

```mermaid
sequenceDiagram
    participant Actor as Actor (human/API/CLI/MCP) or Lifecycle Hook/Plugin
    participant AF as Issue Artifacts (IArtifactService)
    participant Store as IArtifactStore (SQLite BLOB-backed)
    participant IBS as Issue & Board Service
    participant AR as Audit & Recovery

    Actor->>AF: AttachArtifactAsync(issueId, kind, title, contentReference, source, actorId?, metadata?)
    AF->>IBS: Verify issue exists / actor authorized
    IBS-->>AF: OK
    AF->>Store: Store(contentReference) [if content is inline-provided, e.g. a file upload]
    Store-->>AF: Opaque reference confirmed
    AF->>AF: Persist Artifact row
    AF->>AR: Emit ArtifactAttached(issueId, artifactId, source)
    AF-->>Actor: Artifact DTO

    Note over Store: On store failure: ARTIFACT_STORE_UNAVAILABLE,<br/>no partial/corrupt Artifact row is persisted

    Actor->>AF: RefreshArtifactAsync(issueId, kind, dedupKey, contentReference, metadata, source) [pull_request only]
    AF->>AF: Find existing Artifact by (issueId, kind, dedupKey)
    alt existing artifact found
        AF->>AF: Update ContentReference/Metadata in place (no new row)
        AF->>AR: Emit ArtifactRefreshed(issueId, artifactId, source)
    else no existing artifact
        AF->>AF: Persist new Artifact row
        AF->>AR: Emit ArtifactAttached(issueId, artifactId, source)
    end
    AF-->>Actor: Artifact DTO
```

## Key Behaviors

### `AttachArtifactAsync(issueId, kind, title, contentReference, source?, actorId?, metadata?)` (planned; new)

1. Validate `issueId` resolves to an existing issue in the caller's authorized workspace — `REFERENCED_ENTITY_NOT_FOUND` if not.
2. Validate `kind` is one of `file`, `link`, `deployment`, `pull_request` — `VALIDATION_FAILED` naming the invalid value otherwise. (This is a small closed set describing *artifact shape*, not the free-form `Type`/`Priority` taxonomy on `Issue` — it is not workspace-configurable.)
3. Validate `title` and `contentReference` are non-empty — `VALIDATION_FAILED` naming the missing field otherwise.
4. If `contentReference` represents inline content the caller wants durably stored (e.g., an uploaded file's bytes) rather than an already-external URL, call `IArtifactStore.StoreAsync(...)` to obtain the durable opaque reference; a store failure surfaces as `ARTIFACT_STORE_UNAVAILABLE` and no `Artifact` row is persisted (fail-closed — never a partial/corrupt attachment). `metadata` (when present) is persisted verbatim alongside the row and is never passed to `IArtifactStore` — it is small, structured, and lives directly on the `Artifacts` row.
5. Persist the `Artifact` row: `source` defaults to `"local"` when `actorId` is provided and `source` is omitted; when called by a `Post*` lifecycle hook, `actorId` is omitted and `source` must identify the originating hook/integration key (e.g., `slack-thread-expansion`, `github`).
6. Emit `ArtifactAttached` audit/activity event with `(issueId, artifactId, kind, source)` — never raw content.
7. Return the `Artifact` DTO.

### `ListArtifactsAsync(issueId)` (planned; new)

Returns all `Artifact` rows for the issue ordered by `CreatedAt` ascending (oldest first, matching comment ordering conventions in `issue-board-service`). `REFERENCED_ENTITY_NOT_FOUND` if the issue does not exist or is outside the caller's workspace.

### `RefreshArtifactAsync(issueId, kind, dedupKey, contentReference, metadata, source)`

Realization of FR-ART-001 idempotent upsert capability:

1. Validate `issueId` resolves to an existing issue in the caller's authorized workspace — `REFERENCED_ENTITY_NOT_FOUND` if not.
2. Validate `kind` is a refreshable kind (currently only `pull_request`) — `VALIDATION_FAILED` otherwise; refresh is not a generic operation available to `file`/`link`/`deployment` artifacts.
3. Look up an existing `Artifact` matching `(issueId, kind, dedupKey)`, where `dedupKey` is kind-specific (for `pull_request`, the PR URL). If found, update its `ContentReference`/`Metadata` in place and emit `ArtifactRefreshed` — no new row is created and `CreatedAt`/`AddedById` are unchanged. If not found, behave exactly as `AttachArtifactAsync` (persist a new row, emit `ArtifactAttached`) so the very first PR event always succeeds.
4. This operation is idempotent: refreshing with identical `contentReference`/`metadata` values is a no-op from the caller's perspective (still emits `ArtifactRefreshed` for audit completeness, but no user-visible content changes).
5. Return the `Artifact` DTO.

### `RemoveArtifactAsync(issueId, artifactId, actorId)` (planned; new)

1. Validate the artifact exists and belongs to the given issue — `REFERENCED_ENTITY_NOT_FOUND` otherwise.
2. Delete the `Artifact` row. Deletion is a metadata-level removal; whether the underlying `IArtifactStore` content is immediately purged or retained per a documented retention/archive policy is a store-implementation decision (the SQLite BLOB-backed store purges the associated BLOB on removal; a future implementation may instead archive) — either way, removal is never silent: an `ArtifactRemoved` audit event is always emitted (FR-ART-001 AC4).
3. `RemoveArtifactAsync` requires the same authorization the issue mutation itself requires — there is no separate "artifact admin" role.

### Automated artifact expansion

Realization of FR-ART-002 automated expansion capability via lifecycle hooks:

A `Post*` `ILifecycleHook<TEvent>` (owned by `integration-and-plugin-platform`, e.g. registered for `PostAddComment`) that recognizes a linked external resource (e.g., a Slack thread permalink pasted into a comment or description) calls `AttachArtifactAsync` exactly as described above, with constraints specific to automated expansion:

1. **Same budget/authorization/audit path**: the hook's call is subject to the same `LifecycleHookOptions` execution budget, and produces the same `ArtifactAttached` audit event, as any other hook-driven mutation — there is no separate, higher-privilege "expansion" write path (FR-ART-002 AC1).
2. **Distinct automated provenance**: `source` is set to the hook/integration key (e.g., `slack-thread-expansion`), never `"local"`, and `actorId` is omitted — so a manually-attached artifact and an automation-attached one are always distinguishable in the returned DTO.
3. **Fail-closed on partial expansion**: if the hook's fetch of the external resource fails partway through, no `Artifact` row is persisted — the failure is reported only as a health/audit diagnostic on the hook/integration, never as a partial or corrupted artifact visible to the issue (FR-ART-002 AC3).
4. **Idempotent re-expansion**: a second expansion of the same source URL on the same issue updates the existing `Artifact`'s `ContentReference` (dedup key: `(IssueId, Source, ContentReference-origin-URL)`) rather than creating a duplicate row, consistent with `integration-and-plugin-platform.md`'s artifact-expansion hook pattern.

### GitHub pull-request artifact correlation

Realization of FR-ART-001 and FR-ART-002 via the GitHub plugin (owned by `integration-and-plugin-platform`):

The GitHub plugin correlates a pull request to an issue and keeps a `pull_request`-kind artifact current via `RefreshArtifactAsync`, with constraints specific to this refreshable kind:

1. **Dedup key is `(IssueId, Kind, PrUrl)`**: unlike other kinds, a `pull_request` artifact is expected to be written multiple times over its lifetime (opened → synchronized → checks completed → merged/closed); `RefreshArtifactAsync` always targets the same row rather than accumulating a new artifact per event (FR-ART-001 refresh operation).
2. **`Metadata` carries live status**: `{ number, state, checksStatus }` (or provider-equivalent fields) is the only place PR status lives — this component does not re-fetch from GitHub itself; the plugin is the sole writer of `Metadata` for this kind.
3. **Same budget/authorization/audit path**: exactly as automated artifact expansion above — a PR refresh is validated and audited identically to a manual attach, distinguished only by `source = "github"` and `actorId` omitted.
4. **First event attaches, later events refresh**: the very first correlated PR event (typically PR-opened) has no existing artifact to match, so `RefreshArtifactAsync` attaches a new row exactly as `AttachArtifactAsync` would (FR-ART-001 idempotent upsert).

## Constraints

- **No content bypass**: this component's service layer never reads/writes artifact content directly — every content operation goes through `IArtifactStore`.
- **No privileged automation path**: a lifecycle hook's `AttachArtifactAsync`/`RefreshArtifactAsync` call is validated, authorized-context-checked, and audited identically to a human/API caller's call; only the `actorId`/`source` values differ.
- **Fail-closed on store errors**: a storage failure never results in a partially-persisted or corrupt `Artifact` row; the write is atomic (store succeeds and the row is persisted, or neither happens).
- **Small closed `Kind` set**: `Kind` (`file`/`link`/`deployment`/`pull_request`) is a fixed enumeration describing artifact shape, not workspace-configurable — unlike `Issue.Type`/`Issue.Priority`, which are deliberately free-form.
- **`Metadata` is opaque and kind-scoped**: only refreshable kinds (`pull_request`) populate `Metadata`; this component does not validate its internal shape beyond a size bound — the owning plugin (GitHub) is solely responsible for what it writes there.
- **Refresh is not a public write path**: `RefreshArtifactAsync` is reachable only from the owning plugin's correlation logic, never from a human/API/CLI/MCP surface directly — a human wanting to "edit" a PR artifact's status has no such operation; status only ever reflects what GitHub reports.
- **Audit on every mutation**: attach, refresh, and remove all always emit an audit/activity event; there is no "silent" artifact operation.
- **Removal never silently purges outside policy**: content retention/archive behavior on removal must be documented per `IArtifactStore` implementation and must not vary undocumented between implementations.

## Acceptance Criteria

| AC-ID | Priority | Criterion | Expected Result | Verification Method |
|-------|----------|-----------|-----------------|---------------------|
| AC-ART-101 | P1 | Given an authorized actor attaching a file artifact to an existing issue, when `AttachArtifactAsync` is called with a valid kind/title/contentReference. | An `Artifact` row is persisted with `Source = "local"` and the calling actor's `AddedById`; an `ArtifactAttached` audit event is emitted; the `Artifact` DTO is returned. | Integration — `ArtifactServiceTests.AttachArtifact_PersistsWithActorProvenanceAndAudit` (FR-ART-001 AC1). |
| AC-ART-102 | P1 | Given an attach request with an unrecognized `kind` value (e.g. `"screenshot"`), when `AttachArtifactAsync` is called. | `VALIDATION_FAILED` is returned naming the invalid `kind`; no `Artifact` row is persisted. | Negative — `ArtifactServiceTests.InvalidKind_RejectsWithValidationFailed` (boundary for FR-ART-001 AC1). |
| AC-ART-103 | P1 | Given an issue with three attached artifacts, when `ListArtifactsAsync` is called across REST, CLI, and MCP surfaces. | All three artifacts are returned in `CreatedAt`-ascending order identically across all three surfaces. | Integration — `ArtifactServiceTests.ListArtifacts_ConsistentAcrossSurfaces` (FR-ART-001 AC2). |
| AC-ART-104 | P1 | Given an artifact attach request whose `IArtifactStore.StoreAsync` call fails (simulated store outage), when `AttachArtifactAsync` is called. | `ARTIFACT_STORE_UNAVAILABLE` is returned; no partial or corrupt `Artifact` row exists afterward. | Integration/fault-injection — `ArtifactServiceTests.StoreFailure_NoPartialArtifactPersisted` (FR-ART-001 AC3, negative). |
| AC-ART-105 | P1 | Given an existing artifact, when `RemoveArtifactAsync` is called by an authorized actor. | The `Artifact` row is removed (or archived per the active store's documented policy); an `ArtifactRemoved` audit event is emitted; content is never silently purged outside that documented policy. | Integration — `ArtifactServiceTests.RemoveArtifact_AuditedAndPolicyCompliant` (FR-ART-001 AC4). |
| AC-ART-106 | P2 | Given an enrichment hook expanding a Slack thread link found in an issue's description, when the hook completes successfully. | An `Artifact` is attached with `Source = "slack-thread-expansion"` and `AddedById = NULL`, distinguishable from a manual attachment; the hook's mutation carries the same audit trail as a manual attach. | Integration — `ArtifactExpansionHookTests.SlackThreadExpansion_AttachesWithAutomatedProvenance` (FR-ART-002 AC1/AC2). |
| AC-ART-107 | P2 | Given an enrichment hook's external fetch fails partway through an expansion attempt, when the hook reports the failure. | No `Artifact` row is created for the failed attempt; the failure is recorded only as a health/audit diagnostic on the hook/integration. | Integration/fault-injection — `ArtifactExpansionHookTests.PartialFetchFailure_NoArtifactPersisted` (FR-ART-002 AC3, negative). |
| AC-ART-108 | P2 | Given an enrichment hook re-expanding the same source URL on the same issue a second time, when the hook runs again. | The existing `Artifact`'s `ContentReference` is updated in place; no duplicate `Artifact` row is created. | Integration — `ArtifactExpansionHookTests.RepeatExpansion_UpdatesExistingArtifactIdempotently`. |
| AC-ART-109 | P2 | Given a `RemoveArtifactAsync` call for an `artifactId` that does not belong to the given `issueId` (belongs to a different issue), when the call is made. | `REFERENCED_ENTITY_NOT_FOUND` is returned; no artifact is removed from either issue. | Negative — `ArtifactServiceTests.RemoveArtifact_WrongIssueScope_NotFound` (FR-ART-001 AC5). |
| AC-ART-110 | P1 | Given the GitHub plugin correlates a newly opened pull request to an issue with no existing `pull_request` artifact, when `RefreshArtifactAsync` is called. | A new `Artifact` row is persisted with `Kind = pull_request`, `Source = "github"`, and `Metadata = { number, state, checksStatus }`; an `ArtifactAttached` audit event is emitted. | Integration — `GitHubPullRequestArtifactSyncTests.FirstPrEvent_AttachesNewArtifact` (FR-ART-001 AC1 realization: pull-request artifact attachment). |
| AC-ART-111 | P1 | Given an issue already has a `pull_request` artifact for a given PR URL, when a subsequent PR webhook event (e.g., checks completed) triggers `RefreshArtifactAsync` with the same `dedupKey`. | The existing `Artifact` row's `Metadata`/`ContentReference` are updated in place; no duplicate row is created; an `ArtifactRefreshed` audit event is emitted. | Integration — `GitHubPullRequestArtifactSyncTests.SubsequentPrEvent_RefreshesInPlace` (FR-ART-001 AC1 realization: pull-request artifact refresh). | (boundary). |

## Error Handling

Every anticipated failure resolves to a §7.7 catalog code; no raw store I/O exception may propagate past this component.

| Condition | Code | HTTP status | Notes |
|---|---:|---|---|
| Issue referenced by `issueId` does not exist or is outside the caller's workspace | `REFERENCED_ENTITY_NOT_FOUND` | 404 | Also applies to `artifactId` not belonging to the given `issueId`. |
| `kind` is not one of `file`/`link`/`deployment`/`pull_request` | `VALIDATION_FAILED` | 400 | Names the invalid `kind` value. |
| `RefreshArtifactAsync` called with a non-refreshable `kind` (not `pull_request`) | `VALIDATION_FAILED` | 400 | Names the kind and states refresh only applies to `pull_request` artifacts. |
| `title` or `contentReference` missing/empty | `VALIDATION_FAILED` | 400 | Names the missing field. |
| `IArtifactStore` read/write failure (store unreachable/unavailable) | `ARTIFACT_STORE_UNAVAILABLE` | 502 | State that artifact storage is temporarily unavailable; retry after the store recovers (tech-design §7.7). |
| Actor lacks permission for the issue's workspace | `WORKSPACE_ACCESS_DENIED` | 403 | Enforced by `workspace-authorization` upstream; this component never re-derives it. |

## File Structure

```
src/
├── Anvilboard.Domain/
│   └── Artifact.cs                       # Planned: Id/IssueId/Kind/Title/ContentReference/Source/AddedById/CreatedAt/Metadata entity (Metadata nullable, populated only for pull_request kind)
├── Anvilboard.Application/
│   └── Artifacts/
│       ├── IArtifactService.cs           # Planned: AttachArtifactAsync/ListArtifactsAsync/RemoveArtifactAsync/RefreshArtifactAsync contract
│       └── ArtifactService.cs            # Planned: implementation, calls IArtifactStore + IIssueService authorization checks
├── Anvilboard.Infrastructure/
│   └── Artifacts/
│       ├── IArtifactStore.cs             # Planned: Store/Retrieve/Delete-by-opaque-reference contract
│       └── SqliteArtifactStore.cs        # Planned: first-release SQLite BLOB-backed implementation
└── Anvilboard.Api/
    └── Endpoints/
        └── ArtifactEndpoints.cs          # Planned: GET/POST /api/v1/issues/{id}/artifacts, DELETE .../{artifactId} (RefreshArtifactAsync has no endpoint — plugin-only)
```

See `integration-and-plugin-platform.md`'s File Structure for the owning `GitHubPullRequestArtifactSync.cs` plugin component that calls `RefreshArtifactAsync`.

## Test Module

**Test file**: `src/Anvilboard.Application.Tests/Artifacts/ArtifactServiceTests.cs`

**Test scope**:
- **Unit**: `kind` validation (accepted values `file`/`link`/`deployment`/`pull_request`, rejection of an unrecognized value), required-field validation (`title`, `contentReference`), provenance defaulting (`source = "local"` when `actorId` provided and `source` omitted).
- **Integration**: attach → list → remove round-trip against a seeded `AnvilboardDbContext` and a real `SqliteArtifactStore`; audit-event emission assertions for both attach and remove; cross-surface (REST/CLI/MCP) consistency of `ListArtifactsAsync` ordering.
- **Fault-injection**: a fake `IArtifactStore` configured to throw on `StoreAsync`, asserting `ARTIFACT_STORE_UNAVAILABLE` and no partial `Artifact` row.
- **Fixtures / Mocks**: seeded `Issue` rows across two workspaces (to test cross-workspace/cross-issue scoping negatives); an in-memory or temp-file-backed `SqliteArtifactStore` instance per test.

**Test file**: `src/Anvilboard.Application.Tests/Artifacts/ArtifactExpansionHookTests.cs`

**Test scope**:
- **Integration**: a fake artifact-expansion `Post*` `ILifecycleHook<TEvent>` that calls `AttachArtifactAsync` with `source = "slack-thread-expansion"` and no `actorId`, asserting the resulting `Artifact` is distinguishable from a manual attachment and carries an identical audit trail shape; idempotent re-expansion updating an existing artifact rather than duplicating it; a simulated partial-fetch failure asserting no `Artifact` row is created.
- **Fixtures / Mocks**: fake external-fetch client returning configurable success/partial-failure responses; a fake `LifecycleHookOptions` budget configuration reused from `integration-and-plugin-platform`'s test fixtures for consistency.

**Test file**: `src/Anvilboard.Application.Tests/Artifacts/RefreshArtifactAsyncTests.cs`

**Test scope**:
- **Unit**: refresh rejects non-refreshable `kind` values with `VALIDATION_FAILED`.
- **Integration**: first PR event attaches a new `pull_request` artifact with `Metadata` populated and `ArtifactAttached` emitted; a second PR event with the same `dedupKey` updates the row in place and emits `ArtifactRefreshed` instead of creating a duplicate; idempotent no-op refresh (identical `contentReference`/`metadata`) still emits `ArtifactRefreshed` but changes no visible content.
- **Fixtures / Mocks**: a fake `GitHubPullRequestArtifactSync` caller supplying `(issueId, dedupKey, metadata)` triples simulating PR lifecycle events (opened → checks completed → merged).
