# Issue Linking

> Feature spec for Spec-Forge implementation planning.
> Source: extracted from docs/anvilboard/tech-design.md §8.1, §10.1 `IssueLinks` table
> Created: 2026-09-05

| Field | Value |
|-------|-------|
| Component | issue-linking |
| Priority | P2 |
| SRS Refs | FR-LNK-001 |
| Tech Design Ref | §8.1 — Issue Linking row; also §7.7 Error Catalog, §9.1 API Design, §10.1 `IssueLinks` table |
| Depends On | issue-board-service, workspace-authorization |
| Blocks | agent-and-automation-surface |

## Purpose

Issue Linking lets an authorized actor or automation record a directional relationship between two issues, expressed as a short `Type` token (for example `RELATED`, `PARENT`, `DUPLICATE`, `MENTIONED_IN`, `BLOCKS`) plus a free-form `Description` string (for example `RELATED` — `"same parent"`, or `PARENT` — `""`), without creating a formal sub-issue hierarchy, ownership cascade, or workflow/completion cascade. It exists because Anvilboard deliberately does not support true sub-issues (per the product decision captured in `docs/anvilboard/prd.md`): teams that need parent/child-shaped context can express it as a typed link, but the system never derives workflow, ownership, or notification behavior from that link — links (including the `BLOCKS` dependency marker) are informational and navigational only, never enforced.

## Scope

**Included:**
- Creating a directional link between two issues with a `Type` token — a short, suggested-vocabulary string (`RELATED`, `PARENT`, `DUPLICATE`, `MENTIONED_IN`, `BLOCKS`, non-exhaustive, not server-enforced) — plus a free-form `Description` string that annotates the link (e.g. `RELATED` — `"same parent"`; `PARENT` — `""`) (FR-LNK-001 AC1).
- Ticket dependency markers via the reserved `BLOCKS` type, reusing this same link table rather than a separate dependency model — `A BLOCKS B` and its inverse "`A` is blocked by `B`" (surfaced via `direction` on `B`) are both expressed as one directional `BLOCKS` row, exactly like `PARENT`/"child of" is expressed as one directional `PARENT` row.
- Listing and removing links on an issue, exposed consistently across web, REST, CLI, and MCP, matching the conventions used by other issue sub-resources (FR-LNK-001 AC2).
- Exposing a link bidirectionally: even though storage is directional (`SourceIssueId` → `TargetIssueId`), both linked issues surface the relationship for discoverability (FR-LNK-001 AC4). Callers pair the returned `type` with the returned `direction` to render inverse phrasing for asymmetric types — e.g. `PARENT`+`outgoing` → "parent of", `PARENT`+`incoming` → "child of"; `BLOCKS`+`outgoing` → "blocks", `BLOCKS`+`incoming` → "blocked by" — without the server maintaining separate reason strings per direction.
- Enforcing a unique constraint on `(SourceIssueId, TargetIssueId, Type)` to prevent duplicate identical links; `Description` is annotation-only and does not participate in the uniqueness key.

**Excluded:**
- Any workflow, ownership, or notification behavior automatically derived from a link's `Type` — links never trigger a transition, reassign an owner, or generate a notification (FR-LNK-001 AC3). This is a hard product boundary, not an implementation gap.
- **Enforcement or gating of `BLOCKS` dependencies** — a `BLOCKS` link is a marker only; it never prevents a phase transition, never blocks a "start work" action, and is never checked automatically before any operation. Surfacing it as a visual cue (e.g. a "blocked" badge) is a display-layer concern for `issue-board-service`, not an enforcement mechanism here.
- True sub-issue hierarchies (cascading completion, cascading workflow state, or structural nesting) — explicitly out of scope per product decision; `PARENT` is just a type token like any other, carrying no special system behavior beyond direction-aware rendering hints.
- Validating or constraining the *vocabulary* of `Type` beyond non-empty, or validating `Description` at all — the suggested vocabulary is a UI/UX affordance (e.g., an autocomplete list), never a server-side enum (FR-LNK-001 AC1).
- Issue CRUD, workflow-state legality, and concurrency control (owned by `issue-board-service`; this component is a sub-resource of an issue, not an independent aggregate).
- Cross-workspace linking — both `SourceIssueId` and `TargetIssueId` must resolve within the same workspace as the requesting actor's authorized scope; cross-workspace issue linking is not supported in this release.

## Core Responsibilities

1. **Link CRUD** — create, list, and remove directional links between two issues, validating both issues exist and are in the same workspace.
2. **Bidirectional exposure** — surface every link from both the source and target issue's perspective, regardless of which issue the link was created "from," with `direction` letting callers render type-appropriate inverse phrasing (e.g. `PARENT`/`incoming` → "child of", `BLOCKS`/`incoming` → "blocked by").
3. **Duplicate prevention** — enforce the `(SourceIssueId, TargetIssueId, Type)` unique constraint, rejecting an exact-duplicate link attempt with a clear conflict error rather than silently creating a second row.
4. **Zero cascade guarantee** — ensure no code path in this component (or any caller of it) derives workflow, ownership, or notification behavior from a link's `Type` — including the `BLOCKS` dependency marker, which is never enforced or gated; this is enforced by design (no such hooks exist) and verified by acceptance tests.
5. **Audit emission** — emit an activity/audit event for every link creation and removal, routed through the same audit path as any other issue mutation.

## Interfaces

### Inputs
- **`CreateLinkAsync(sourceIssueId, targetIssueId, type, description?, actorId?)`** — via `POST /api/v1/issues/{id}/links` and equivalent CLI/MCP operations; `description` defaults to an empty string when omitted; `actorId` is omitted when created by an automation/hook.
- **`ListLinksAsync(issueId)`** — via `GET /api/v1/issues/{id}/links`; returns links where the given issue is either the source or the target.
- **`RemoveLinkAsync(issueId, linkId, actorId)`** — via `DELETE /api/v1/issues/{id}/links/{linkId}`.

### Outputs
- **`IssueLink` DTO** — `(id, sourceIssueId, targetIssueId, type, description, createdById, createdAt, direction)`, where `direction` is a response-shaping field (`outgoing`/`incoming`) computed relative to the issue the list request was scoped to, so callers can render "this issue is a `PARENT` of that issue" (outgoing) versus "this issue is a child of that issue" (incoming, same row) correctly without re-deriving direction client-side.
- **Audit/activity events** — `IssueLinkCreated`/`IssueLinkRemoved`, consumed by `audit-and-recovery`, carrying actor (or automation key), both issue IDs, the `type`, and the `description`. These events also feed the rich activity-history templating in `issue-board-service` (e.g. rendering "arjen linked COM-234 (`RELATED`)" with a clickable reference to `COM-234`).

### Dependencies
- **`issue-board-service`** — supplies issue existence/workspace-scoping/authorization context for both the source and target issue.
- **`workspace-authorization`** — supplies the authorized actor context for manual link create/remove operations.

## Data Flow

```mermaid
sequenceDiagram
    participant Actor as Actor (human/API/CLI/MCP) or Automation
    participant LK as Issue Linking (IIssueLinkService)
    participant IBS as Issue & Board Service
    participant AR as Audit & Recovery

    Actor->>LK: CreateLinkAsync(sourceIssueId, targetIssueId, type, description?, actorId?)
    LK->>IBS: Verify both issues exist, same workspace, actor authorized
    IBS-->>LK: OK
    LK->>LK: Check UNIQUE(SourceIssueId, TargetIssueId, Type)
    alt duplicate link
        LK-->>Actor: RESOURCE_ALREADY_EXISTS (409)
    else new link
        LK->>LK: Persist IssueLink row
        LK->>AR: Emit IssueLinkCreated(sourceIssueId, targetIssueId, type, description)
        LK-->>Actor: IssueLink DTO
    end

    Note over LK: No workflow/ownership/notification<br/>behavior is ever derived from `type` (incl. BLOCKS)
```

## Key Behaviors

### `CreateLinkAsync(sourceIssueId, targetIssueId, type, description?, actorId?)` (planned; new)

1. Validate `sourceIssueId` and `targetIssueId` both resolve to existing issues in the same, caller-authorized workspace — `REFERENCED_ENTITY_NOT_FOUND` if either does not.
2. Validate `sourceIssueId != targetIssueId` — `VALIDATION_FAILED` (an issue cannot link to itself).
3. Validate `type` is a non-empty string — `VALIDATION_FAILED` otherwise. No further validation is applied: `type` is accepted verbatim even if it is not one of the suggested vocabulary values (`RELATED`, `PARENT`, `DUPLICATE`, `MENTIONED_IN`, `BLOCKS`, ...), per FR-LNK-001 AC1 ("without rejecting an unlisted value"). `description` is optional free text with no validation beyond a max-length guard shared with other free-text fields; it defaults to an empty string.
4. Check the `(SourceIssueId, TargetIssueId, Type)` unique constraint — `RESOURCE_ALREADY_EXISTS` (409) naming the conflicting link if an identical link already exists. `description` does not participate in this check — a second `CreateLinkAsync` call with the same `(source, target, type)` but a different `description` is still a duplicate and is rejected, not merged. Note the constraint is *directional*: `(A, B, "RELATED")` and `(B, A, "RELATED")` are distinct rows (both may legitimately exist if created independently from each side, though the UI/typical client flow only needs to create one, since listing is bidirectional). The same directionality is how `BLOCKS` expresses both halves of a dependency: `(A, B, "BLOCKS")` reads as "A blocks B" from A and "blocked by A" from B — no separate `BlockedBy` row or type is needed.
5. Persist the `IssueLink` row: `createdById` set when `actorId` is provided; omitted when created by an automation, matching the artifact-provenance convention in `docs/features/artifacts.md`.
6. Emit `IssueLinkCreated` audit/activity event with `(sourceIssueId, targetIssueId, type, description)`.
7. Return the `IssueLink` DTO.

### `ListLinksAsync(issueId)` (planned; new)

1. Validate `issueId` resolves to an existing issue in the caller's authorized workspace — `REFERENCED_ENTITY_NOT_FOUND` otherwise.
2. Query all `IssueLink` rows where `issueId` matches either `SourceIssueId` or `TargetIssueId`.
3. For each result, compute `direction`: `outgoing` when `issueId == SourceIssueId`, `incoming` when `issueId == TargetIssueId` — this is how bidirectional exposure is implemented without duplicating storage (FR-LNK-001 AC4). Clients combine `type` + `direction` to render type-appropriate inverse phrasing (e.g. `PARENT`/`outgoing` → "parent of", `PARENT`/`incoming` → "child of"; `BLOCKS`/`outgoing` → "blocks", `BLOCKS`/`incoming` → "blocked by") — this component does not maintain separate inverse-phrase strings server-side.
4. Return the list ordered by `CreatedAt` ascending.

### `RemoveLinkAsync(issueId, linkId, actorId)` (planned; new)

1. Validate the link exists and has `issueId` as either its `SourceIssueId` or `TargetIssueId` — `REFERENCED_ENTITY_NOT_FOUND` otherwise (removal can be initiated from either linked issue, consistent with bidirectional exposure).
2. Delete the `IssueLink` row. Removal is a single-row delete — it never cascades to the linked issue itself or to any other link that issue participates in (zero cascade guarantee).
3. Emit `IssueLinkRemoved` audit/activity event.

## Constraints

- **Zero cascade, always**: no `Type` value — including `PARENT`, `DUPLICATE`, or `BLOCKS` — ever triggers a workflow transition, ownership change, or notification. This is enforced structurally (no such hook exists in this component) rather than by runtime check, and is covered by a dedicated negative acceptance test.
- **`BLOCKS` is a marker, never a gate**: a `BLOCKS` link records that the author considers one issue a prerequisite of another; it has no runtime effect anywhere in the system — it never prevents a phase transition, never disables a "start work" action, and is checked by nothing except an optional read-only display cue in `issue-board-service`. This is the same zero-enforcement guarantee as every other `Type`, called out explicitly because "blocks" reads as if it should gate something.
- **`Type` is short and suggested-vocabulary, never a server-side enum**: the suggested vocabulary (`RELATED`, `PARENT`, `DUPLICATE`, `MENTIONED_IN`, `BLOCKS`, ...) is advisory only; an unrecognized string is accepted, not rejected. `Description` is unrestricted free text and is never validated against any vocabulary.
- **Directional storage, bidirectional exposure**: `(SourceIssueId, TargetIssueId)` is fixed at creation time; `ListLinksAsync` computes `direction` per query rather than maintaining two mirrored rows. Inverse phrasing (e.g. "child of", "blocked by") is a client-side rendering concern derived from `(type, direction)`, not separate stored data.
- **No true sub-issue hierarchy**: this component never enforces or infers structural nesting, multi-level cascades, or aggregate rollups from `PARENT`/`DUPLICATE` types — those tokens carry exactly as much system meaning as `RELATED` does.
- **Same-workspace only**: both `SourceIssueId` and `TargetIssueId` must resolve within the same workspace; cross-workspace links are out of scope for this release.
- **Audit on every mutation**: link creation and removal both always emit an audit/activity event.

## Acceptance Criteria

| AC-ID | Priority | Criterion | Expected Result | Verification Method |
|-------|----------|-----------|-----------------|---------------------|
| AC-LNK-101 | P2 | Given two existing issues in the same workspace, when `CreateLinkAsync` is called with `type = "RELATED"`, `description = "same parent"`. | An `IssueLink` row is persisted directionally (`SourceIssueId` → `TargetIssueId`) with both `type` and `description`; an `IssueLinkCreated` audit event is emitted; the `IssueLink` DTO is returned. | Integration — `IssueLinkServiceTests.CreateLink_PersistsDirectionallyWithAudit` (FR-LNK-001 AC1/AC2). |
| AC-LNK-102 | P2 | Given a link-creation request with an unlisted `type` value (e.g. `"BLOCKS_ON_REVIEW"`), when `CreateLinkAsync` is called. | The link is created successfully; the unlisted type string is not rejected. | Integration — `IssueLinkServiceTests.UnlistedType_IsAccepted` (FR-LNK-001 AC1, explicit non-rejection case). |
| AC-LNK-103 | P2 | Given an existing link `(A, B, "RELATED")`, when `CreateLinkAsync` is called again with the identical `(A, B, "RELATED")` pair (regardless of `description`). | `RESOURCE_ALREADY_EXISTS` (409) is returned naming the conflicting link; no duplicate row is created, even when the new call's `description` differs from the existing row's. | Negative — `IssueLinkServiceTests.DuplicateLink_RejectsWithConflict` (unique-constraint boundary; `description` excluded from the key). |
| AC-LNK-104 | P2 | Given a link `(A, B, "PARENT")`, when `ListLinksAsync(A)` and `ListLinksAsync(B)` are both called. | `ListLinksAsync(A)` returns the link with `direction = "outgoing"` (renderable as "parent of"); `ListLinksAsync(B)` returns the *same* link with `direction = "incoming"` (renderable as "child of") — both issues surface the relationship. | Integration — `IssueLinkServiceTests.ListLinks_BidirectionallyDiscoverableFromBothIssues` (FR-LNK-001 AC4). |
| AC-LNK-105 | P2 | Given a link between issue A and issue B with `type = "PARENT"`, when issue A transitions workflow state, is reassigned, or is archived. | Issue B's workflow state, owner, and notification state are all unaffected — no cascade of any kind occurs. | Integration/negative — `IssueLinkServiceTests.ParentType_NeverCascadesWorkflowOwnershipOrNotification` (FR-LNK-001 AC3, explicit zero-cascade guarantee). |
| AC-LNK-106 | P2 | Given a link-creation request where `sourceIssueId == targetIssueId`. | `VALIDATION_FAILED` is returned; no link is created. | Negative — `IssueLinkServiceTests.SelfLink_RejectsWithValidationFailed` (boundary). |
| AC-LNK-107 | P2 | Given a link-creation request where `targetIssueId` belongs to a different workspace than the caller's authorized workspace. | `REFERENCED_ENTITY_NOT_FOUND` is returned; no link is created; no cross-workspace issue existence is disclosed. | Negative — `IssueLinkServiceTests.CrossWorkspaceTarget_NotFound` (FR-LNK-001 boundary / workspace isolation). |
| AC-LNK-108 | P2 | Given a link `(A, B, "RELATED")`, when `RemoveLinkAsync` is called scoped to issue B (the target, not the source). | The link is removed successfully; an `IssueLinkRemoved` audit event is emitted; issue A and issue B are otherwise unaffected. | Integration — `IssueLinkServiceTests.RemoveLink_CallableFromEitherLinkedIssue` (FR-LNK-001 AC2/AC4). |
| AC-LNK-109 | P2 | Given issue A and issue B with a link `(A, B, "BLOCKS")` recorded, when issue B's workflow phase is changed to any phase (including a phase that would normally imply "in progress" or "done"). | The phase change succeeds unconditionally — the `BLOCKS` link has no gating effect and no `Pre*PhaseChange` hook check is performed against it; the link remains purely informational/displayable. | Negative — `IssueLinkServiceTests.BlocksType_NeverGatesPhaseTransition` (explicit no-enforcement guarantee for the dependency marker). |

## Error Handling

Every anticipated failure resolves to a §7.7 catalog code; no raw EF Core exception may propagate past this component.

| Condition | Code | HTTP status | Notes |
|---|---:|---|---|
| `sourceIssueId` or `targetIssueId` does not exist, or resolves outside the caller's workspace | `REFERENCED_ENTITY_NOT_FOUND` | 404 | Applies identically to a cross-workspace target, avoiding existence disclosure across workspace boundaries. |
| `sourceIssueId == targetIssueId` | `VALIDATION_FAILED` | 400 | An issue cannot link to itself. |
| `type` missing/empty | `VALIDATION_FAILED` | 400 | Names the missing field; any non-empty value is otherwise accepted. `description` has no missing/empty error case — it is optional and defaults to `""`. |
| Duplicate `(SourceIssueId, TargetIssueId, Type)` | `RESOURCE_ALREADY_EXISTS` | 409 | Names the conflicting link (tech-design §7.7 UNIQUE-constraint translation pattern); a differing `description` on the new request does not avoid the conflict. |
| `linkId` does not belong to the given `issueId` (neither source nor target) | `REFERENCED_ENTITY_NOT_FOUND` | 404 | Applies on removal. |
| Actor lacks permission for the issue's workspace | `WORKSPACE_ACCESS_DENIED` | 403 | Enforced by `workspace-authorization` upstream; this component never re-derives it. |

## File Structure

```
src/
├── Anvilboard.Domain/
│   └── IssueLink.cs                      # Planned: Id/SourceIssueId/TargetIssueId/Type/Description/CreatedById/CreatedAt entity
├── Anvilboard.Application/
│   └── IssueLinks/
│       ├── IIssueLinkService.cs          # Planned: CreateLinkAsync/ListLinksAsync/RemoveLinkAsync contract
│       └── IssueLinkService.cs           # Planned: implementation, calls IIssueService for existence/workspace checks
└── Anvilboard.Api/
    └── Endpoints/
        └── IssueLinkEndpoints.cs         # Planned: GET/POST /api/v1/issues/{id}/links, DELETE .../{linkId}
```

## Test Module

**Test file**: `src/Anvilboard.Application.Tests/IssueLinks/IssueLinkServiceTests.cs`

**Test scope**:
- **Unit**: `type` non-empty validation, self-link rejection, unlisted-type acceptance (explicit non-rejection assertion), `description` defaulting to `""` when omitted, direction computation logic for a given `issueId`.
- **Integration**: create → list (from both sides) → remove round-trip against a seeded `AnvilboardDbContext`; unique-constraint conflict on duplicate `(source, target, type)` regardless of differing `description`; cross-workspace target rejection; audit-event emission assertions for both create and remove.
- **Negative / zero-cascade**: a dedicated test seeding a `PARENT`/`DUPLICATE` link and asserting no workflow transition, owner reassignment, or notification occurs on either linked issue as a *side effect* of any operation on the other — this guards the product's explicit "no sub-issue hierarchy" boundary against accidental future coupling.
- **Negative / no-enforcement (`BLOCKS`)**: a dedicated test seeding a `BLOCKS` link and asserting a phase change on the blocked issue succeeds unconditionally with no `Pre*PhaseChange` hook veto or gating check attributable to this component — guards the explicit "marker only, never a gate" boundary.
- **Fixtures / Mocks**: seeded `Issue` rows across two workspaces (for cross-workspace negative tests); at least one pair of issues linked with each suggested vocabulary type (`RELATED`, `PARENT`, `DUPLICATE`, `MENTIONED_IN`, `BLOCKS`), each with a representative `description`, to exercise DTO/list rendering.
