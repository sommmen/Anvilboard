# Workspace Authorization

> Feature spec for code-forge implementation planning.
> Source: extracted from docs/anvilboard/tech-design.md §8.1
> Created: 2026-09-05

| Field | Value |
|-------|-------|
| Component | workspace-authorization |
| Priority | P0 |
| SRS Refs | FR-WS-001, NFR-SEC-002 |
| Tech Design Ref | §8.1 Workspace & Authorization; §11.2 Authorization |
| Depends On | — |
| Blocks | workflow-engine, issue-board-service, integration-and-plugin-platform, agent-and-automation-surface, audit-and-recovery |

## Purpose

Workspace Authorization is the single, non-duplicated enforcement point that authenticates every human and programmatic actor and authorizes every read or mutation against a workspace-scoped role before any other component runs. It resolves the requested `WorkspaceId`, evaluates the actor's role permissions, and rejects any request that reads or mutates data outside the actor's granted workspace — the foundational cross-cutting concern the rest of the design builds on (`docs/anvilboard/tech-design.md` §3.2 "Introduce workspace-scoped authentication/authorization as the foundational cross-cutting concern").

## Scope

**Included:**
- Authenticating REST, CLI, MCP, and web-session requests against a stored credential (opaque hashed API token for agents; HTTP-only secure session cookie for the SPA).
- Resolving the requested `WorkspaceId` and the calling actor's `Role` (`Administrator`, `Coordinator`, `Contributor`, `AutomationAgent`) within that workspace.
- Evaluating the RBAC permission map for the requested operation and returning an authorization decision.
- The first-administrator bootstrap flow (creating the first workspace/administrator when none exists).
- Credential/session issuance, expiry, and immediate administrator-triggered revocation.
- Emitting authorization decisions (denials, and security-relevant configuration authorizations) for the Audit & Recovery component to persist.

**Excluded:**
- Workflow state/transition validation rules (see `workflow-engine.md`).
- Persisting the `AuditEvents` row itself — this component only raises the decision (see `audit-and-recovery.md`).
- An external identity provider / SSO integration — the exact credential mechanism is an open decision (`docs/anvilboard/tech-design.md` §11.1); this component implements the local-credential path only.
- Per-field business-rule guards on issues, workflows, or integrations (enforced by their own components once Workspace Authorization has already granted access).

## Core Responsibilities

1. **Actor authentication** — validate a bearer API token (REST/CLI/MCP) or session cookie (web) and resolve it to a `Member` or agent principal.
2. **Workspace scope resolution** — bind every authenticated request to exactly one `WorkspaceId`; no query or mutation may span workspaces.
3. **Role-based permission evaluation** — check the actor's `Role` against the requested `Permission` using the static role/permission map (§11.2).
4. **Bootstrap flow** — allow the very first administrator/workspace to be created without a pre-existing credential, then close that path permanently.
5. **Credential lifecycle** — issue, expire, and immediately revoke sessions and API tokens; revocation takes effect on the next request, not at next expiry check.
6. **Authorization decision emission** — publish every denial and every security-relevant configuration authorization to `IAuditService` for persistence.

## Interfaces

### Inputs
- **Channel credential** (`Anvilboard.Api` request pipeline; `Anvilboard.Agent` CLI/MCP invocation context) — bearer API token, session cookie, or (bootstrap only) an unauthenticated first-administrator request.
- **Requested workspace + action** (from the calling REST endpoint, CLI command, or MCP tool call) — `WorkspaceId` plus the `Permission` the operation requires.
- **Configuration changes to members/roles/tokens** (from an already-authorized Administrator) — role assignment, token issuance, and revocation requests.

### Outputs
- **`ActorContext`** (to `Anvilboard.Api` middleware, CLI command dispatch, MCP tool-call dispatch) — the authenticated, workspace-scoped principal (`MemberId` or agent token id, `WorkspaceId`, `Role`, `IsAgent`) attached to the request for downstream components to read but never to re-derive.
- **`AuthorizationResult`** (to the calling channel) — `Authorized` or `Denied` with one of `AUTHENTICATION_REQUIRED`, `CREDENTIAL_INVALID_OR_EXPIRED`, `WORKSPACE_ACCESS_DENIED`.
- **Authorization decision events** (to `IAuditService`, see `audit-and-recovery.md`) — every denial and every security-relevant configuration authorization, each carrying actor, workspace, action, outcome, and correlation ID (FR-WS-001 AC4).

### Dependencies
- **Domain** (`Workspace`, `Member`, `Role`) — the entities a decision is evaluated against.
- **Anvilboard.Infrastructure** (`AnvilboardDbContext`) — persisted workspaces, members, roles, and API tokens.
- **Audit & Recovery** (`IAuditService`) — receives authorization decisions; this component never writes `AuditEvents` rows directly.

## Data Flow

```mermaid
sequenceDiagram
    participant Actor as Human / Automation Agent
    participant Channel as REST / CLI / MCP entry point
    participant Auth as WorkspaceAuthorizationService
    participant Store as AnvilboardDbContext
    participant Audit as Audit & Recovery

    Actor->>Channel: Request with credential (bearer token / session cookie) + workspaceId
    Channel->>Auth: AuthenticateAsync(credential)
    Auth->>Store: Resolve Member or ApiToken by credential hash
    Store-->>Auth: Member/ApiToken + Role (or none found)
    Auth->>Auth: AuthorizeAsync(actorContext, workspaceId, requiredPermission)
    alt credential missing or malformed
        Auth-->>Channel: Denied(AUTHENTICATION_REQUIRED)
    else credential invalid, expired, or revoked
        Auth-->>Channel: Denied(CREDENTIAL_INVALID_OR_EXPIRED)
    else role lacks permission or wrong workspace
        Auth->>Audit: RecordAuthorizationDecision(denied, correlationId)
        Auth-->>Channel: Denied(WORKSPACE_ACCESS_DENIED)
    else authorized
        Auth->>Audit: RecordAuthorizationDecision(authorized, correlationId) — mutations and security-relevant config only
        Auth-->>Channel: Authorized(ActorContext)
    end
    Channel-->>Actor: Downstream result or structured error
```

## Key Behaviors

### `AuthenticateAsync`

`Task<AuthenticationResult> AuthenticateAsync(ChannelCredential credential, CancellationToken ct = default)`

1. If `credential` is null, empty, or not one of the two accepted shapes (bearer token string, session cookie value) → return `AuthenticationResult.Failed(AUTHENTICATION_REQUIRED)`.
2. Hash the presented token/cookie value using the same hash algorithm used at rest (never compare plaintext) and look up a matching, non-expired `ApiToken`/session row scoped by hash.
3. If no match is found, or the match is expired, or the match has `RevokedAt` set → return `AuthenticationResult.Failed(CREDENTIAL_INVALID_OR_EXPIRED)`.
4. Otherwise resolve the owning `Member` (or agent principal) and its `Role`, and return `AuthenticationResult.Succeeded(ActorContext)`.

### `AuthorizeAsync`

`Task<AuthorizationResult> AuthorizeAsync(ActorContext actor, WorkspaceId workspaceId, Permission action, CancellationToken ct = default)`

1. If `actor.WorkspaceId != workspaceId` → return `AuthorizationResult.Denied(WORKSPACE_ACCESS_DENIED)` without loading or returning any workspace/issue/member data (FR-WS-001 AF-2).
2. Look up `action` in the static `RolePermissionMap[actor.Role]` set (see mapping table below).
3. If `action` is not present in the set → return `AuthorizationResult.Denied(WORKSPACE_ACCESS_DENIED)`.
4. Otherwise → return `AuthorizationResult.Authorized()`.
5. For every mutation and every security-relevant configuration read, call `IAuditService.RecordAuthorizationDecisionAsync(actor, workspaceId, action, outcome, correlationId, ct)` regardless of outcome (FR-WS-001 AC4); read-only board/dashboard queries that succeed are not individually audited (matches §11.4 "Events logged").

### Role → Permission map (§11.2)

| Role | Permissions | Notes |
|---|---|---|
| `Administrator` | Full workspace configuration, integration/plugin management, backup/restore, audit read | Only role that can archive/replace workflow states and revoke credentials. |
| `Coordinator` | Read/write issues, board/dashboard, integration health read | Cannot change workspace configuration or secrets. |
| `Contributor` | Read/write assigned or team-scoped issues, comments | Cannot reassign issues outside their team scope. |
| `AutomationAgent` | Scoped read/write per issued token's granted permission subset; never secret read | The subset is fixed at token-issuance time; `AuthorizeAsync` intersects the role default with the token's granted subset. |

### Bootstrap flow (FR-WS-001 Preconditions)

`Task<ActorContext> BootstrapFirstAdministratorAsync(BootstrapRequest request, CancellationToken ct = default)`

1. Query whether any `Workspace` row exists. If at least one exists → reject with `VALIDATION_FAILED` naming "bootstrap already completed" (prevents privilege escalation after go-live).
2. Create the `Workspace`, a `Member` with `Role = Administrator`, and an initial credential (hashed password or minted API token) in one transaction.
3. Emit an audit event for the bootstrap action (actor = the new administrator, action = `WORKSPACE_BOOTSTRAPPED`).
4. Return the new `ActorContext` so the caller can proceed to workspace/team/workflow configuration (which then requires normal `AuthorizeAsync` checks).

### Credential revocation

`Task RevokeCredentialAsync(WorkspaceId workspaceId, MemberId actorPerformingRevocation, CredentialId credentialId, CancellationToken ct = default)` — requires `AuthorizeAsync(actor, workspaceId, Permission.ManageCredentials)` to have already succeeded. Sets `RevokedAt = DateTimeOffset.UtcNow` on the credential row; the next `AuthenticateAsync` call using that credential fails with `CREDENTIAL_INVALID_OR_EXPIRED` — revocation is never deferred to a background sweep.

### Symbolic identifiers

`Role` and `Permission` are serialized externally as UPPER_SNAKE_CASE symbolic strings (`"ADMINISTRATOR"`, `"WORKSPACE_ACCESS_DENIED"`), matching §7.2 API Naming; the internal C# enum member names use PascalCase per §7.2 Code Naming.

## Constraints

- **Single enforcement point**: `WorkspaceAuthorizationService` lives in `Anvilboard.Application` and is invoked identically by REST middleware, CLI command dispatch, and MCP tool-call dispatch — no endpoint, command, or tool handler may perform its own ad hoc authorization check (§11.2).
- **No cross-workspace queries**: every repository call scoped through this component carries `WorkspaceId`; a query that could span workspaces is a defect, not a configuration choice (§11.2).
- **Secret handling**: raw API tokens and session values are never persisted, logged, or returned after issuance; only the salted hash is stored (NFR-SEC-001).
- **Open decision — credential mechanism**: local username/password vs. a pluggable SSO-ready provider abstraction is unresolved (`docs/anvilboard/tech-design.md` §11.1); this spec implements the local-credential path and leaves an `ICredentialProvider` seam for the future SSO decision.
- **Open decision — access-denied status code**: whether cross-workspace access should uniformly return 403 `WORKSPACE_ACCESS_DENIED` or 404 in some contexts is tracked as OQ-001 (`docs/anvilboard/tech-design.md` §17); this spec defaults to the uniform 403 behavior described in AC-002/§7.7 until that decision is finalized.
- **Transport security**: TLS 1.2+ is required for REST in supported production deployments (§11.3); local/dev loopback exceptions are documented separately.

## Acceptance Criteria

> Rows AC-001/AC-002 are mapped from `docs/anvilboard/tech-design.md` §3.6. All other rows are component-specific.

| AC-ID | Priority | Criterion | Expected Result | Verification Method |
|-------|----------|-----------|-----------------|---------------------|
| AC-001 | P0 | Given an authenticated actor whose role grants the requested action in the requested workspace, when the actor calls `AuthorizeAsync` | Returns `Authorized()`; the downstream call proceeds and returns its normal result | Integration: `WorkspaceAuthorizationServiceTests` cross-product of role × action × workspace membership |
| AC-002 | P0 | Given an authenticated actor requesting a workspace it is not a member of | Returns `WORKSPACE_ACCESS_DENIED`; response contains no issue, member, or workspace fields | Integration + contract test: assert HTTP 403, error code, and response body has no `data` field, across REST/CLI/MCP |
| AC-101 | P0 | Given a request with no `Authorization` header/session cookie | Returns `AUTHENTICATION_REQUIRED` (401); no workspace lookup is attempted | Unit: `AuthenticateAsync` with null credential |
| AC-102 | P0 | Given a request with a revoked or expired API token | Returns `CREDENTIAL_INVALID_OR_EXPIRED` (401) | Unit: `AuthenticateAsync` with a token whose `RevokedAt`/`ExpiresAt` has passed |
| AC-103 | P0 | Given a valid, non-revoked credential but a role lacking the requested permission in the correct workspace | Returns `WORKSPACE_ACCESS_DENIED` (403); no data returned | Unit: `AuthorizeAsync` with `Role.Contributor` requesting an `Administrator`-only permission |
| AC-104 | P1 | Given any denial (AUTHENTICATION_REQUIRED, CREDENTIAL_INVALID_OR_EXPIRED, or WORKSPACE_ACCESS_DENIED) or a successful mutation/config authorization | Exactly one authorization-decision event is emitted to `IAuditService` with actor, workspace, action, outcome, and correlation ID | Integration: assert `IAuditService.RecordAuthorizationDecisionAsync` call count and payload per branch |
| AC-105 | P0 | Given no `Workspace` row exists yet, when `BootstrapFirstAdministratorAsync` is called | Creates the workspace, the first `Member` with `Role = Administrator`, and returns a valid `ActorContext` | Integration: bootstrap against an empty `AnvilboardDbContext` |
| AC-106 | P0 | Given at least one `Workspace` row already exists, when `BootstrapFirstAdministratorAsync` is called again | Rejected with `VALIDATION_FAILED` naming "bootstrap already completed"; no second workspace/administrator is created | Negative integration test: bootstrap twice, assert single workspace count |
| AC-107 | P1 | Given an administrator revokes an agent's API token, when that agent immediately replays a request using the same token | Returns `CREDENTIAL_INVALID_OR_EXPIRED` on the very next request (not deferred to expiry) | Integration: revoke then replay within the same test, assert immediate denial |
| AC-108 | P0 | Given any `ActorContext` or `AuthorizationResult` is serialized to a log, response, or audit payload | The raw API token/session value never appears in the output; only non-secret identifiers (`MemberId`, `Role`, masked token id) are present | Negative test: run `run_secret_scanning`-style assertion over serialized `ActorContext`/log output fixtures |

## Error Handling

- **`AUTHENTICATION_REQUIRED`** (401) — missing or malformed credential on any protected channel entry point; never discloses whether the requested workspace exists.
- **`CREDENTIAL_INVALID_OR_EXPIRED`** (401) — credential hash has no live match, or the matched credential is expired or revoked; message states the credential is invalid/expired without naming which reason, to avoid confirming token validity ranges.
- **`WORKSPACE_ACCESS_DENIED`** (403) — valid actor, wrong workspace or insufficient role permission; response omits every workspace/issue/member field (AC-002).
- **`VALIDATION_FAILED`** (400) — bootstrap attempted when a workspace already exists; malformed bootstrap payload.
- Lower-layer `DbUpdateException`/constraint violations encountered while resolving credentials are caught at the `Anvilboard.Application` boundary and translated per §7.6; they never reach a channel as a raw exception or a `500` response for any of the anticipated failures above.

## File Structure

```
src/
├── Anvilboard.Domain/
│   ├── Role.cs                                       # New: Role enum (Administrator, Coordinator, Contributor, AutomationAgent)
│   ├── Permission.cs                                 # New: Permission enum + RolePermissionMap
│   ├── ApiToken.cs                                   # New: hashed, workspace-scoped credential entity (agents + sessions)
│   ├── Member.cs                                     # Modified: adds Role property
│   └── Ids.cs                                        # Modified: adds ApiTokenId
├── Anvilboard.Application/
│   └── Authorization/
│       ├── IWorkspaceAuthorizationService.cs         # New: AuthenticateAsync / AuthorizeAsync / BootstrapFirstAdministratorAsync contract
│       ├── WorkspaceAuthorizationService.cs          # New: single enforcement-point implementation (see Key Behaviors)
│       ├── ActorContext.cs                           # New: authenticated + authorized actor record
│       ├── AuthenticationResult.cs                   # New: Succeeded/Failed result + error code
│       └── AuthorizationResult.cs                    # New: Authorized/Denied result + error code
├── Anvilboard.Infrastructure/
│   ├── Persistence/
│   │   ├── AnvilboardDbContext.cs                    # Modified: adds ApiTokens DbSet
│   │   └── Configurations/
│   │       ├── MemberConfiguration.cs                # Modified: adds Role column mapping
│   │       └── ApiTokenConfiguration.cs              # New: EF Core configuration for ApiToken (hash unique index)
│   └── Migrations/
│       └── {timestamp}_AddWorkspaceAuthorization.cs  # New: adds Role column, ApiTokens table
├── Anvilboard.Api/
│   └── Authorization/
│       └── WorkspaceAuthorizationMiddleware.cs       # New: ASP.NET Core middleware calling AuthenticateAsync/AuthorizeAsync before any endpoint
└── Anvilboard.Agent/
    └── Authorization/
        └── AgentCredentialResolver.cs                # New: resolves the bearer API token from CLI/MCP invocation context
```

## Test Module

**Test file**: `src/Anvilboard.Application.Tests/Authorization/WorkspaceAuthorizationServiceTests.cs`

**Test scope**:
- **Unit**: `AuthenticateAsync()` (missing credential, unknown hash, expired, revoked, valid), `AuthorizeAsync()` (role × permission × workspace-match matrix), `BootstrapFirstAdministratorAsync()` (empty store, already-bootstrapped store), `RevokeCredentialAsync()` (immediate effect on next `AuthenticateAsync`).
- **Integration**: `src/Anvilboard.Api.Tests/Authorization/WorkspaceAuthorizationEndpointTests.cs` using `WebApplicationFactory<Program>` — cross-workspace denial returns 403 with no resource fields, missing-credential returns 401, audit event recorded on every denial and mutation authorization.
- **Fixtures / Mocks**: `AnvilboardDbContext` backed by a fresh SQLite file or `Microsoft.Data.Sqlite` in-memory connection per test, seeded with two workspaces and one `Member` per `Role` in each; a fake `IAuditService` capturing recorded decisions for assertion.
