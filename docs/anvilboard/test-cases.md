# Test Cases: Anvilboard

> **Status:** Target-state QA specification (Spec Mode). The current proof of concept has no automated test projects; all cases below are planned coverage and must not be interpreted as existing passing tests.
>
> **Source chain:** [PRD](./prd.md) → [SRS](./srs.md) → [Technical Design](./tech-design.md) → [feature specifications](../features/overview.md).

## 1. Functional Inventory

### 1.1 Project Overview

| Field | Value |
|---|---|
| **Project** | Anvilboard |
| **Project Type** | Multi-workspace issue-management web application with REST, CLI, MCP, provider integration, and plugin surfaces |
| **Tech Stack** | .NET 8 / ASP.NET Core, Angular, EF Core, SQLite for supported single-host deployment |
| **Test Framework** | Target: xUnit, `WebApplicationFactory`, EF Core SQLite fixtures, Angular component tests; contract tests for CLI and MCP |
| **Scan Date** | 2026-05-09 |
| **Input Mode** | Spec Mode, grounded in the repository’s current PoC |

### 1.2 Testable Units

| # | Unit / boundary | Type | Planned test location | Existing tests | Coverage status |
|---:|---|---|---|---|---|
| 1 | `WorkspaceAuthorizationService` | application service | `Application.Tests/Authorization/WorkspaceAuthorizationServiceTests.cs` | No | None |
| 2 | protected REST endpoints | API boundary | `Api.Tests/Authorization/WorkspaceAuthorizationEndpointTests.cs` | No | None |
| 3 | `WorkflowEngine` and legacy-status migration | domain service / migration | `Application.Tests/Workflows/WorkflowEngineTests.cs`; `Infrastructure.Tests/Migrations/LegacyStatusMigrationTests.cs` | No | None |
| 4 | `IssueService`, `BoardQueryService`, `DashboardService` | application services | `Application.Tests/Issues/IssueServiceTests.cs`; `Application.Tests/Dashboard/DashboardServiceTests.cs` | No | None |
| 5 | `SyncCoordinator`, webhook receivers, plugin registry | integration boundary | `Application.Tests/Sync/SyncCoordinatorTests.cs`; provider and infrastructure test projects | No | None |
| 6 | idempotency, API v1, CLI and MCP contract adapters | application / transport | `Application.Tests/Automation/IdempotencyServiceTests.cs`; `Api.Tests/V1/AutomationSurfaceContractTests.cs`; `Agent.Tests/ContractEquivalenceTests.cs` | No | None |
| 7 | audit redaction, backup and restore | operations services | `Application.Tests/Audit/AuditServiceTests.cs`; `Infrastructure.Tests/Audit/BackupServiceTests.cs` | No | None |

### 1.3 Coverage Summary

| Metric | Value |
|---|---:|
| Target testable boundaries | 7 |
| Boundaries with existing automated tests | 0 |
| Planned test cases | 46 |
| Current automated coverage | 0% (no test projects exist) |

### 1.4 Interaction Map

| Unit A | Relationship | Unit B | Risk |
|---|---|---|---|
| authorization service | gates all reads and mutations | issue, workflow, audit, restore services | Critical |
| issue service | validates and records changes | workflow engine, audit service, integrations | Critical |
| sync coordinator | invokes provider adapters and persists external work | issue service, audit service | Critical |
| automation adapters | canonicalize a request | idempotency service, issue service, error translator | Critical |
| backup service | restores a verified snapshot | authorization service, audit service, persistence | Critical |

## 2. Test Strategy

### 2.1 Test Pyramid

| Level | Target | Rationale |
|---|---:|---|
| Unit (pure/domain logic) | 45% | Workflow guards, permission evaluation, canonical request hashing, redaction, pagination and catalog translation need fast exhaustive feedback. |
| Unit (I/O-touching service) | 30% | Services require SQLite-backed verification of persistence, uniqueness, versioning and audit writes. |
| Integration / contract | 20% | REST, CLI and MCP parity; middleware authorization; migration; provider webhook and plugin loading must be tested at real boundaries. |
| End-to-end / performance | 5% | A small production-like suite validates dashboard query latency, supported backup recovery, and cross-service chains. |

### 2.2 Environments, data and isolation

- Use a fresh SQLite database per test fixture; use real relational constraints rather than EF Core’s in-memory provider for integrity, migration, and transaction tests.
- Use deterministic clocks, correlation IDs, provider delivery IDs, and fixture credentials. Never place real provider secrets in test input, logs, snapshots, or fixtures.
- Substitute GitHub and Linear HTTP clients with a controllable fake server; inject timeout, retryable HTTP failure, duplicate delivery, malformed signature, and cursor scenarios.
- Execute REST integration tests with `WebApplicationFactory`; execute CLI and MCP adapters through their public protocol boundaries. MCP tests must parse stdout as protocol output and inspect diagnostics separately.
- Validate every external failure through the canonical catalog in [Technical Design §7.7](./tech-design.md), including code, status, safe message, and correlation ID where applicable.

### 2.3 Test dimensions

| Dimension | Values represented |
|---|---|
| Actor / role | unauthenticated, invalid credential, viewer, contributor, workspace administrator, automation credential |
| Workspace scope | owning workspace, another workspace, nonexistent / unauthorized reference |
| Workflow | active initial state, allowed target, disallowed target, inactive / archived state |
| Transport | REST v1, CLI, MCP |
| Provider state | healthy, duplicate delivery, invalid signature, timeout / retry exhausted, paused integration, incompatible / failing plugin |
| Persistence | new record, duplicate key, stale version, transaction failure, valid backup, corrupt / incompatible backup |
| Query | first page, cursor boundary, scoped filter, empty result, dashboard reconciliation |

### 2.4 Planned suite conventions

Tests use IDs in this document as the stable planning identifier. Test names should encode the public behavior (for example, `RequestTransitionAsync_DisallowedTarget_ReturnsInvalidWorkflowTransition`). A catalog assertion must verify the complete public error contract, not merely a numeric HTTP response.

## 3. Test Cases — Unit and Boundary

### 3.1 Workspace authorization and workflow

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-AUTH-001 | Authorization | Valid workspace credential authenticates and resolves its actor and workspace | valid credential, owning workspace | Authenticated identity and effective workspace are returned; no other scope is inferred. | P0 | service fixture | Planned |
| TC-AUTH-002 | Authorization | Missing protected-channel credential is rejected | unauthenticated | `401 AUTHENTICATION_REQUIRED`; no handler or data access runs. | P0 | REST/CLI/MCP fixture | Planned |
| TC-AUTH-003 | Authorization | Expired or invalid credential is rejected safely | invalid credential | `401 CREDENTIAL_INVALID_OR_EXPIRED`; response exposes neither credential material nor workspace data. | P0 | credential fixture | Planned |
| TC-AUTH-004 | Authorization | Viewer cannot mutate issue or workflow configuration | viewer role | `403 WORKSPACE_ACCESS_DENIED`; no state or audit mutation is written. | P0 | SQLite fixture | Planned |
| TC-AUTH-005 | Authorization | Cross-workspace read and mutation are denied without disclosure | another workspace | `403 WORKSPACE_ACCESS_DENIED`; protected entity identifiers/details are not disclosed. | P0 | two-workspace fixture | Planned |
| TC-AUTH-006 | Authorization | First administrator bootstrap is one-time and auditable | bootstrap | First valid bootstrap succeeds; a second bootstrap is rejected and cannot elevate another actor. | P0 | SQLite fixture | Planned |
| TC-WF-001 | Workflow | Allowed transition updates issue state and activity | allowed target | State changes to target and emits required activity/audit intent. | P0 | domain + SQLite fixture | Planned |
| TC-WF-002 | Workflow | Disallowed transition returns cataloged conflict | disallowed target | `409 INVALID_WORKFLOW_TRANSITION` identifies current state, target, and rule; issue remains unchanged. | P0 | domain fixture | Planned |
| TC-WF-003 | Workflow | Archived or inactive state cannot be selected | inactive state | `409 INVALID_WORKFLOW_TRANSITION`; no issue update occurs. | P0 | SQLite fixture | Planned |
| TC-WF-004 | Workflow | Legacy status migration maps supported historical values deterministically | migration | Valid legacy values map to defined workflow states; unmappable values halt migration with actionable diagnostics rather than silent data loss. | P1 | migration SQLite fixture | Planned |

### 3.2 Issue, board and dashboard

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-ISSUE-001 | Issue | Create valid issue with workspace-unique key | new record | Returns issue with workspace, key, title and initial workflow state; exactly one audit entry is requested. | P0 | SQLite fixture | Planned |
| TC-ISSUE-002 | Issue | Missing or malformed required issue input is rejected | validation | `400 VALIDATION_FAILED` names the invalid field; no partial issue, activity, or audit record exists. | P0 | API fixture | Planned |
| TC-ISSUE-003 | Issue | Duplicate workspace issue key is rejected | duplicate key | `409 RESOURCE_ALREADY_EXISTS`; only the original issue remains. | P0 | SQLite fixture | Planned |
| TC-ISSUE-004 | Issue | Stale expected version cannot overwrite a concurrent update | stale version | `409 CONCURRENCY_CONFLICT`; persisted newer content and version remain intact. | P0 | SQLite fixture | Planned |
| TC-ISSUE-005 | Issue | Assignment and comment mutation preserve actor, time, and workspace activity | mutation | Correct activity records and audit intent are produced with no cross-workspace relation. | P1 | SQLite fixture | Planned |
| TC-BOARD-001 | Board query | Workspace-scoped filter and cursor pagination return stable page boundaries | query, cursor | Results contain only authorized workspace items; next cursor neither duplicates nor skips items. | P0 | seeded SQLite fixture | Planned |
| TC-BOARD-002 | Board query | Invalid pagination or filter input returns validation contract | boundary | `400 VALIDATION_FAILED`; server does not silently coerce malformed input. | P1 | API fixture | Planned |
| TC-DASH-001 | Dashboard | Summary counts reconcile with the same authorized board-query result set | query, workspace | Counts by state/assignee equal independently queried board results for the same filters. | P0 | seeded SQLite fixture | Planned |

### 3.3 Integration and plugin platform

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-SYNC-001 | Sync | New provider item upserts once with provenance and cursor progress | healthy provider | Creates/updates one local issue, records provider provenance, and advances cursor only after durable processing. | P0 | fake provider + SQLite | Planned |
| TC-SYNC-002 | Sync | Duplicate external delivery is idempotently deduplicated | duplicate delivery | Repeated delivery creates no second issue, activity, or audit mutation. | P0 | fake provider + SQLite | Planned |
| TC-SYNC-003 | Sync | One source failure is isolated from other configured sources | failing source | Failing source records health/backoff state; healthy source loop continues. | P0 | fault-injection fixture | Planned |
| TC-SYNC-004 | Sync | Retry budget exhaustion becomes provider-unavailable contract | timeout / retry exhausted | `502 PROVIDER_UNAVAILABLE` at actionable boundary; retry uses bounded backoff and no raw transport exception escapes. | P0 | fake clock/provider | Planned |
| TC-SYNC-005 | Sync | Paused integration refuses deliberate sync action | paused integration | `409 INTEGRATION_PAUSED`; cursor and local issues stay unchanged. | P1 | SQLite fixture | Planned |
| TC-WEBHOOK-001 | Webhook | Invalid GitHub or Linear signature is rejected before ingestion | invalid signature | Request is rejected without calling the sync/issue service or persisting payload. | P0 | signed request fixture | Planned |
| TC-PLUGIN-001 | Plugins | Incompatible plugin contract version is skipped safely | incompatible plugin | Plugin is unavailable with diagnostic health state; host process and compatible plugins continue. | P0 | plugin fixture | Planned |
| TC-PLUGIN-002 | Plugins | Throwing plugin hook is isolated from committed mutation | hook exception | Primary issue mutation and audit commit remain successful; failure is observable without an unhandled exception. | P0 | throwing plugin fixture | Planned |

### 3.4 Automation contracts

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-AUTO-001 | Idempotency | First mutation claim executes and stores canonical request/result | new key | Claim succeeds; completed record binds actor, canonical payload, outcome and correlation ID. | P0 | SQLite fixture | Planned |
| TC-AUTO-002 | Idempotency | Same actor and canonical payload replay returns original result | replay | Second request does not execute mutation or create a second audit entry. | P0 | SQLite fixture | Planned |
| TC-AUTO-003 | Idempotency | Same key with different payload or actor is rejected | key reuse | `409 IDEMPOTENCY_KEY_REUSED`; existing record is unmodified. | P0 | SQLite fixture | Planned |
| TC-AUTO-004 | API / CLI / MCP | Equivalent symbolic request produces equivalent result on all channels | REST, CLI, MCP | Same normalized command/result/error semantics and correlation ID behavior; only transport framing differs. | P0 | contract harness | Planned |
| TC-AUTO-005 | REST API | v1 responses include declared API version and machine-readable error envelope | REST v1 | Success/error response contains contract version, stable code, safe message and correlation ID. | P0 | `WebApplicationFactory` | Planned |
| TC-AUTO-006 | MCP | MCP stdout remains protocol-pure under successful and failing requests | MCP | stdout contains only protocol messages; diagnostics/logs go to the approved diagnostic sink. | P0 | MCP process harness | Planned |
| TC-AUTO-007 | Rate limiter | Exceeded channel limit returns retryable catalog contract | rate limited | `429 RATE_LIMITED` includes `Retry-After`; no raw middleware response replaces catalog envelope. | P1 | fake clock | Planned |

### 3.5 Audit and recovery

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-AUDIT-001 | Audit | Every successful mutation records one immutable audit event | mutation | Exactly one record includes actor, workspace, action, target, correlation ID and redacted payload; it cannot be updated/deleted through application paths. | P0 | SQLite fixture | Planned |
| TC-AUDIT-002 | Audit | Secret-bearing inputs are scrubbed before audit persistence and logs | secret input | Tokens, authorization values, webhook secrets and configured secret patterns have zero plaintext matches in persisted/event output. | P0 | secret-scan fixture | Planned |
| TC-AUDIT-003 | Audit query | Audit query is constrained to authorized workspace | cross-workspace | Caller sees only authorized workspace history; other-workspace rows are absent without disclosure. | P0 | two-workspace fixture | Planned |
| TC-BACKUP-001 | Backup | Verified backup restores a consistent authorized workspace round trip | valid backup | Restore reproduces selected supported data and emits audit event; authorization is rechecked before restore. | P0 | SQLite artifact fixture | Planned |
| TC-BACKUP-002 | Backup | Corrupt, incompatible, schema-invalid, or checksum-invalid artifact fails closed | corrupt backup | `422 BACKUP_INTEGRITY_INVALID`; target data remains unchanged. | P0 | corrupted artifact matrix | Planned |
| TC-BACKUP-003 | Backup | Unauthorized actor cannot restore a valid artifact | insufficient role | `403 WORKSPACE_ACCESS_DENIED`; artifact is not applied and no restore audit is written. | P0 | role fixture | Planned |

### 3.6 Performance and availability

| TC ID | Module | Title | Dimensions | Expected result | Priority | Infra | Automation |
|---|---|---|---|---|---:|---|---|
| TC-PERF-001 | Board/dashboard | Authorized interactive board query and summary meet SRS latency target at documented representative data volume | query load | p95 meets `NFR-PERF-001`; result correctness remains intact under concurrent reads. | P1 | production-like SQLite fixture | Planned |
| TC-PERF-002 | Recovery | Supported single-host recovery completes within the documented recovery objective | valid backup | Restored instance becomes operational within `NFR-AVL-001` target with integrity verification retained. | P1 | isolated host fixture | Planned |

## 4. Test Cases — Combination

| TC ID | Units involved | Title | Expected result | Priority | Infra | Automation |
|---|---|---|---|---:|---|---|
| TC-COMBO-001 | authorization → workflow → issue → audit | Authorized transition is enforced and recorded once | Contributor performs an allowed own-workspace transition; state/activity/audit are coherent. Viewer or foreign-workspace variants produce no partial mutation. | P0 | REST + SQLite | Planned |
| TC-COMBO-002 | webhook → sync → issue upsert → audit | Verified external delivery creates one provenance-preserving issue | Valid delivery creates/updates one issue and one audit event; replay changes neither count. | P0 | fake webhook/provider + SQLite | Planned |
| TC-COMBO-003 | REST/CLI/MCP → canonicalization → idempotency → audit | Cross-channel equivalent replay is one logical mutation | Initial REST request and equivalent CLI/MCP replay return the same logical result and leave one issue/audit event. | P0 | three-channel contract harness | Planned |
| TC-COMBO-004 | backup verification → authorization → restore → audit | Restore validates before any destructive write | Corrupt backup fails before modification; valid authorized restore succeeds and writes a single restore audit event. | P0 | artifact + SQLite | Planned |
| TC-COMBO-005 | plugin hook → issue mutation → failure reporting | Extension failure cannot roll back or conceal primary commit | Mutation/audit commit, hook exception gets contained and correlated operational diagnostic. | P0 | throwing plugin + SQLite | Planned |

## 5. Coverage Matrix

### 5.1 SRS requirement coverage

| Requirement ID | Planned test cases | Coverage |
|---|---|---|
| FR-WS-001 | TC-AUTH-001–005, TC-COMBO-001 | Covered |
| FR-WS-002 | TC-AUTH-006, TC-WF-001–004 | Covered |
| FR-WS-003 | TC-WF-001–003, TC-COMBO-001 | Covered |
| FR-WRK-001 | TC-BOARD-001–002, TC-DASH-001, TC-PERF-001 | Covered |
| FR-WRK-002 | TC-ISSUE-001–005, TC-COMBO-001 | Covered |
| FR-WRK-003 | TC-ISSUE-005, TC-BOARD-001, TC-COMBO-001 | Covered |
| FR-WRK-004 | TC-DASH-001, TC-PERF-001 | Covered |
| FR-INT-001 | TC-SYNC-003–005, TC-WEBHOOK-001 | Covered |
| FR-INT-002 | TC-SYNC-001–004, TC-WEBHOOK-001, TC-COMBO-002 | Covered |
| FR-INT-003 | TC-PLUGIN-001–002, TC-COMBO-005 | Covered |
| FR-AUT-001 | TC-AUTO-004–006, TC-COMBO-003 | Covered |
| FR-AUT-002 | TC-AUTO-001–003, TC-COMBO-003 | Covered |
| FR-AUT-003 | TC-AUTH-002–003, TC-AUTO-005–007 | Covered |
| FR-OPS-001 | TC-AUDIT-001–003, TC-COMBO-001–005 | Covered |
| FR-OPS-002 | TC-BACKUP-001–003, TC-PERF-002, TC-COMBO-004 | Covered |
| NFR-PERF-001 | TC-PERF-001, TC-AUTO-007 | Covered |
| NFR-SEC-001 | TC-AUTH-003, TC-WEBHOOK-001, TC-AUDIT-002 | Covered |
| NFR-SEC-002 | TC-AUTH-004–005, TC-AUDIT-003, TC-BACKUP-003 | Covered |
| NFR-REL-001 | TC-ISSUE-003–004, TC-AUTO-001–003, TC-BACKUP-002 | Covered |
| NFR-REL-002 | TC-SYNC-003–004, TC-PLUGIN-001–002 | Covered |
| NFR-AVL-001 | TC-BACKUP-001–002, TC-PERF-002 | Covered |
| NFR-MNT-001 | TC-SYNC-004, TC-PLUGIN-001, TC-AUTO-007 | Covered |
| NFR-PRT-001 | **Gap:** deployment topology/upgrade test plan is not yet detailed in a feature specification. | Planned gap |
| NFR-USB-001 | TC-AUTH-002–005, TC-WF-002–003, TC-AUTO-005–007, TC-BACKUP-002 | Covered |

### 5.2 Technical-design system acceptance criteria coverage

| Source acceptance criterion | Planned test cases | Coverage |
|---|---|---|
| Tech Design §3.6 AC-001–003 (identity, tenant isolation, role enforcement) | TC-AUTH-001–006, TC-AUDIT-003 | Covered |
| Tech Design §3.6 AC-004–006 (workflow and issue integrity) | TC-WF-001–004, TC-ISSUE-001–005 | Covered |
| Tech Design §3.6 AC-007–008 (versioned, idempotent automation) | TC-AUTO-001–006, TC-COMBO-003 | Covered |
| Tech Design §3.6 AC-009–010 (secure, resilient integration) | TC-SYNC-001–005, TC-WEBHOOK-001, TC-PLUGIN-001–002 | Covered |
| Tech Design §3.6 AC-011–012 (audit and recovery) | TC-AUDIT-001–003, TC-BACKUP-001–003, TC-COMBO-004 | Covered |

### 5.3 Feature-spec acceptance criteria coverage

AC identifiers are intentionally qualified with their source document because several numeric identifiers are reused across feature specifications.

| Feature-spec source | Acceptance criteria | Planned test cases | Coverage |
|---|---|---|---|
| [workspace-authorization.md](../features/workspace-authorization.md) | AC-001–012, AC-101–108 | TC-AUTH-001–006, TC-COMBO-001 | Covered |
| [workflow-engine.md](../features/workflow-engine.md) | AC-003–004, AC-201–208 | TC-WF-001–004, TC-COMBO-001 | Covered |
| [issue-board-service.md](../features/issue-board-service.md) | AC-004–006, AC-011, AC-IBS-101–103 | TC-ISSUE-001–005, TC-BOARD-001–002, TC-DASH-001, TC-COMBO-001–002 | Covered |
| [integration-and-plugin-platform.md](../features/integration-and-plugin-platform.md) | AC-009–010, AC-IPP-101–105 | TC-SYNC-001–005, TC-WEBHOOK-001, TC-PLUGIN-001–002, TC-COMBO-002, TC-COMBO-005 | Covered |
| [agent-and-automation-surface.md](../features/agent-and-automation-surface.md) | AC-007–008, AC-101–105 | TC-AUTO-001–007, TC-COMBO-003 | Covered |
| [audit-and-recovery.md](../features/audit-and-recovery.md) | AC-011–012, AC-201–204 | TC-AUDIT-001–003, TC-BACKUP-001–003, TC-COMBO-004 | Covered |

### 5.4 Error-catalog coverage

| Catalog code | Representative test cases |
|---|---|
| `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED` | TC-AUTH-002–005, TC-BACKUP-003 |
| `VALIDATION_FAILED`, `REFERENCED_ENTITY_NOT_FOUND` | TC-ISSUE-002, TC-BOARD-002; add explicit missing-reference variant when the endpoint contracts are implemented |
| `INVALID_WORKFLOW_TRANSITION`, `RESOURCE_ALREADY_EXISTS`, `CONCURRENCY_CONFLICT` | TC-WF-002–003, TC-ISSUE-003–004 |
| `IDEMPOTENCY_KEY_REUSED`, `RATE_LIMITED` | TC-AUTO-003, TC-AUTO-007 |
| `PROVIDER_UNAVAILABLE`, `INTEGRATION_PAUSED` | TC-SYNC-004–005 |
| `BACKUP_INTEGRITY_INVALID` | TC-BACKUP-002 |

## 6. Gap Analysis and Implementation Order

| Gap | Rationale | Recommendation |
|---|---|---|
| No test projects or runner configuration | The repository currently contains no automated test projects. | Create the planned test projects and shared SQLite/WebApplicationFactory fixture before feature implementation expands. |
| Deployability coverage (`NFR-PRT-001`) | The technical design defines supported deployment but does not yet provide executable deployment/upgrade detail. | Add deployment acceptance criteria and an environment smoke/upgrade test specification before packaging work. |
| Explicit not-found contract exercise | The catalog defines `REFERENCED_ENTITY_NOT_FOUND`, but the feature test modules do not name a concrete endpoint case. | Add endpoint-level missing workflow state/member/reference tests when routes are finalized. |
| UI accessibility and visual workflow coverage | The target Angular interface is described upstream but component-level behavior is not sufficiently detailed in feature specs. | Add UI component/e2e cases after the frontend interaction design is decomposed. |

## 7. Statistics

| Metric | Value |
|---|---:|
| Total planned test cases | 46 |
| P0 critical cases | 39 |
| P1 important cases | 7 |
| Unit/boundary cases | 41 |
| Combination cases | 5 |
| Security-focused cases | 12 |
| Persistence/recovery integrity cases | 11 |
| Open traceability gaps | 3 |
