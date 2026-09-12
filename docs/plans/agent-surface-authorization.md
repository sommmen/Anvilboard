# Implementation Plan: Agent Surface Authorization, Idempotency & Contract Version

> Feature-level technical design and execution plan for the **Agent & Automation Surface**
> hardening block — the largest remaining cluster of Major findings, and the only place in the
> product where a mutation reaches the database without passing an authorization check.
> Generated with Spec-Forge `tech-design-generation` against the canonical chain:
> [`prd.md`](../anvilboard/prd.md) → [`srs.md`](../anvilboard/srs.md) →
> [`tech-design.md`](../anvilboard/tech-design.md) →
> [`agent-and-automation-surface.md`](../features/agent-and-automation-surface.md).

## 1. Document Information

| Field | Value |
|---|---|
| Document | Implementation Plan — Agent Surface Authorization, Idempotency & Contract Version |
| Feature component | [`agent-and-automation-surface`](../features/agent-and-automation-surface.md) |
| Milestone | [`tech-design.md`](../anvilboard/tech-design.md) §16 **M4: Automation Contract Normalization** |
| Audit findings | [`audit-report.md`](../audit-report.md) **MAJ-015**, **MAJ-016**, **MAJ-017**, plus the CLI/MCP half of **MAJ-001** — Recommended Priority Action **#6** (and the unstruck part of **#4**) |
| SRS refs | `FR-AUT-002` (primary), `NFR-MNT-001` (primary), `FR-AUT-001` + `FR-AUT-003` + `NFR-SEC-001` + `NFR-SEC-002` (touched) |
| Acceptance criteria | `AC-007`, `AC-008`, `AC-101`, `AC-103`, `AC-105`, and the CLI/MCP half of `AC-102` (from [`agent-and-automation-surface.md`](../features/agent-and-automation-surface.md)) |
| Status | Plan — not yet implemented |
| Created | 2026-09-12 |

## 2. Why this feature was selected

The three Critical findings are closed (CRIT-001/002/003 all `RESOLVED` in
[`audit-report.md`](../audit-report.md)), and priority actions 1–3 are struck through. Of the
remaining actions, **#6 is the only one where the gap is a live security hole rather than a missing
convenience surface**: every other open Major is "a capability the spec promises is absent", whereas
MAJ-015 means an existing, shipped, reachable code path performs workspace mutations with no
authentication, no authorization, and no attributable actor.

| Signal | Finding |
|---|---|
| Open GitHub issues | `gh issue list --state open` → none. The backlog lives in [`docs/features/overview.md`](../features/overview.md) + [`docs/audit-report.md`](../audit-report.md). |
| Unresolved findings | Priority action **#6** (MAJ-015, MAJ-016, MAJ-017) is untouched. Action **#4** is half stale — `RevokeCredentialAsync` and `DELETE /api/auth/credentials/{id}` both exist, so **MAJ-002 is already resolved**; only MAJ-001's CLI/MCP half remains, and it is the same gap as MAJ-015. |
| Code evidence — auth | `rg "AuthenticateAsync\|AuthorizeAsync\|ActorContext" src/Anvilboard.Agent` → **zero matches.** `BoardAgentService` hardcodes `private const string AutomationActorId = "agent:automation"` and passes `actorId: null` / `authorId: null` / `createdById: null` into `IssueService`. |
| Code evidence — idempotency | `rg "IIdempotencyService" src` → the interface, the implementation, and **one DI registration** ([`ServiceCollectionExtensions.cs`](../../src/Anvilboard.Application/ServiceCollectionExtensions.cs) line 42). **Zero call sites** in either host. |
| Code evidence — contract version | `rg "apiVersion" src` → **zero matches.** No response envelope exists on the agent surface; operations return bare DTOs. |
| Code evidence — correlation | The REST host reads `X-Correlation-Id` ([`Anvilboard.Api/Program.cs`](../../src/Anvilboard.Api/Program.cs) lines 40–42) but **never echoes it back**, and only [`BackupEndpoints.cs`](../../src/Anvilboard.Api/Endpoints/BackupEndpoints.cs) injects `CorrelationContext` at all — the agent host uses the `FromHeaderOrNew(null)` fallback and threads its value into exactly one place ([`BoardAgentService.cs`](../../src/Anvilboard.Agent/BoardAgentService.cs) line 49, backups only). `AC-103`/`AC-105` are therefore unmet on the agent surface. |
| Milestone status | M4 is the only `Partial` milestone whose remainder is a security gap: *"`IdempotencyRecord` exists but is not wired through the agent surface"*. |
| Blocked work | `Discover_DoesNotExposeRestore` in [`BoardAgentServiceTests.cs`](../../src/Anvilboard.Agent.Tests/BoardAgentServiceTests.cs) exists **solely because** MAJ-015 is open — the backup plan's §9.3/OQ-P3 defers agent-side restore until this closes. |

### 2.1 Verified current state

Read directly from `src/` at commit `e8e32a7`:

- [`Program.cs`](../../src/Anvilboard.Agent/Program.cs) builds `new OperationInvoker(new ScopedServiceProvider(provider), jsonOptions)` — **no `policies` argument**, so the surface has no invocation gate at all.
- `ScopedServiceProvider.GetService` calls `root.CreateScope().ServiceProvider.GetService(serviceType)` — a **fresh, never-disposed DI scope per service resolution**. There is therefore no per-invocation scope in which an `ActorContext` could currently live.
- All 13 operations on [`BoardAgentService`](../../src/Anvilboard.Agent/BoardAgentService.cs) are implicitly `AgentSafetyLevel.Safe`; `OperationInvoker.EnsureConfirmationIsEnforceable` would throw `ConfirmationPolicyMissingException` for anything above `Safe` while no `IConfirmationEnforcingPolicy` is registered.
- `create-backup` takes a caller-supplied `Guid workspaceId` and forwards it straight to `IBackupService` — with no authentication, **any** caller of the CLI picks any workspace.
- `IssueService.CreateAsync` / `ChangeStatusAsync` / `AssignAsync` / `AddCommentAsync` all accept a nullable actor and record `ActivityEvent`s with it; the agent surface always supplies `null`, so agent-driven activity is anonymous in the activity feed as well as in audit.
- The REST side is the working reference: [`WorkspaceAuthorizationMiddleware`](../../src/Anvilboard.Api/Authorization/WorkspaceAuthorizationMiddleware.cs) authenticates every non-`[AllowAnonymous]` request and authorizes against [`RequiresPermissionAttribute`](../../src/Anvilboard.Api/Authorization/RequiresPermissionAttribute.cs) metadata, whose docstring states endpoints must **never** call `AuthorizeAsync` themselves (§11.2 single enforcement point).

## 3. Overview

### 3.1 Background

`Anvilboard.Agent` is a dual-mode host: one process is either a one-shot CLI (`dotnet run -- issues
list-issues …`) or a long-lived MCP stdio server (`dotnet run -- mcp`), chosen from `args[0]`. Both
modes share one DI container, one `OperationCatalog`, and one `OperationInvoker`, and both dispatch
into [`BoardAgentService`](../../src/Anvilboard.Agent/BoardAgentService.cs) — a thin wrapper over the
same `IssueService` / `IssueLinkService` / `DashboardService` / `IBackupService` the REST host uses.

That sharing is the design's strength (one behaviour, three channels) and, right now, its weakness:
the REST host wraps those services in an authorization middleware, and the agent host wraps them in
nothing. `IWorkspaceAuthorizationService`'s own docstring — *"The single enforcement point … invoked
identically by the REST middleware, the CLI, and the MCP host — no channel is allowed to implement
its own ad hoc checks"* — describes an intent the agent host does not yet honour.

The upstream [`dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface) package
(consumed by sibling-directory `ProjectReference`, see [`DEVELOPMENT.md`](../../DEVELOPMENT.md))
already ships the exact seam this needs, and it is currently unused:

- `IOperationInvocationPolicy.EvaluateAsync(operation, inputs, confirmation, ct, invocationContext)`
  runs **before every invocation**, before argument binding and before the target is resolved, and
  can return `OperationPolicyResult.Deny(error)`.
- `OperationInvocationContext(ClaimsPrincipal? Principal, string? Credential)` is documented upstream
  as *"trusted, host-supplied caller information … deliberately separate from operation inputs;
  callers cannot inject an identity through the JSON argument object."*
- Both `OperationCommandLineAdapter` and `McpOperationAdapter` accept an `OperationInvocationContext`
  in their constructor **and** per call, with the per-call value winning.

So the fix is not to invent an enforcement mechanism — it is to use the one already designed for it.

### 3.2 Goals

1. **G1 — No unauthenticated mutation path.** Every agent operation resolves a real `ActorContext`
   via `IWorkspaceAuthorizationService.AuthenticateAsync` before any application service is reached;
   an unauthenticated invocation fails closed with `AUTHENTICATION_REQUIRED`. (MAJ-015, MAJ-001)
2. **G2 — Declarative, single-point authorization.** Each operation declares the `Permission`(s) it
   needs; one policy enforces them. Adding an operation without a declaration must fail loudly, not
   silently allow. (MAJ-015, mirrors API §11.2)
3. **G3 — Attributable actor identity.** Audit events, activity events, and idempotency records
   carry the specific member **and credential** that acted, not a shared `agent:automation` literal.
   (MAJ-015)
4. **G4 — Safe retries.** Every mutating operation takes an idempotency key, replays the original
   result on an identical retry, and rejects key reuse with a different payload. (MAJ-016, AC-007,
   AC-008, AC-101, FR-AUT-002)
5. **G5 — Versioned, correlatable envelope.** Every agent response carries a non-empty `apiVersion`
   and the invocation's `correlationId`. (MAJ-017, AC-105, AC-103, NFR-MNT-001)
6. **G6 — No stdout contamination.** None of the above may write to stdout in MCP mode. (AC-104)

### 3.3 Non-Goals

- **Not** a REST response-envelope change. Adding `apiVersion`/`data` wrappers to every REST endpoint
  is a separate, larger contract migration with an Angular client to update; see §16.2. The one REST
  change in scope is echoing the resolved `X-Correlation-Id` back as a response header (§9.4); the
  host already *reads* the inbound header.
- **Not** the `/api` → `/api/v1` route-prefix migration. Pre-existing, cross-cutting, already
  recorded as `OQ-P1` in [`backup-and-restore.md`](./backup-and-restore.md) §17.
- **Not** interactive/OAuth credential flows, credential storage, or a `login` command. The agent
  host consumes an API token that already exists; minting and revoking it is the REST host's job
  (`POST`/`DELETE /api/auth/credentials`, already implemented).
- **Not** exposing `restore` on the agent surface. See `DR-AGT-004`.
- **Not** rate limiting the agent surface (`RATE_LIMITED` stays REST-only).

### 3.4 Scope

| In scope | Out of scope |
|---|---|
| Per-invocation DI scope for the agent host | Replacing the DI container or hosting model |
| Credential resolution from configuration/environment | Credential minting, rotation, storage-at-rest |
| `RequiresAgentPermissionAttribute` on all 13 operations | New operations beyond the existing 13 |
| `WorkspaceAuthorizationPolicy : IOperationInvocationPolicy` | `IConfirmationEnforcingPolicy` / dangerous-op UX |
| Actor propagation into `IssueService` / `IBackupService` | Changing those services' signatures |
| `IIdempotencyService` wiring on the 6 mutating operations | REST-side idempotency (§16.2) |
| `AgentResponse<T>` envelope with `apiVersion` + `correlationId` | REST response envelope (§16.2) |
| `X-Correlation-Id` echo as a REST response header | REST pagination/filter contract work (`FR-AUT-001`) |
| Removing the spoofable `workspaceId` parameter from `create-backup` | Multi-workspace agent sessions |

### 3.5 User Scenarios

1. **Coding agent with a scoped token.** An MCP host is configured with
   `ANVILBOARD_AGENT__APITOKEN`. It calls `create-issue`; the token resolves to an
   `AutomationAgent`-role member holding `ReadWriteIssues`; the issue is created with
   `CreatedById` set to that member, and the audit/activity trail names the token.
2. **Token without the needed permission.** The same host calls `create-backup`. The token's
   effective permissions (role defaults ∩ token grants) exclude `ManageBackupRestore`, so the call
   is denied with `WORKSPACE_ACCESS_DENIED` before `IBackupService` is touched, and the denial is
   audited.
3. **Unconfigured host.** A developer runs the CLI with no token configured. Every operation —
   including reads — fails with `AUTHENTICATION_REQUIRED` and a message pointing at the
   configuration key. Nothing leaks about whether a workspace or member exists.
4. **Retry after a timeout.** An agent's `create-issue` call times out client-side. It retries with
   the same `idempotencyKey` and identical arguments; the original `IssueSummary` is replayed, and
   no second issue, activity event, or audit event is produced.
5. **Key collision.** The agent reuses yesterday's key with a different title; it receives
   `IDEMPOTENCY_KEY_REUSED` and no mutation occurs.
6. **Version negotiation.** An MCP host reads `apiVersion` from any response and can branch on it
   before a future breaking change lands.

### 3.6 Acceptance Criteria

Sourced verbatim-in-intent from
[`agent-and-automation-surface.md`](../features/agent-and-automation-surface.md); IDs preserved.

| AC-ID | Criterion | How this plan satisfies it |
|---|---|---|
| `AC-007` | Same actor + key + canonical payload replays the original result with no duplicate side effects | §8.4 `AgentIdempotency.ExecuteAsync` — `TryBeginAsync` returns `ReplayOriginal`, the wrapped delegate is never invoked |
| `AC-008` | Key reuse with a different payload or actor → `IDEMPOTENCY_KEY_REUSED`, no mutation | §8.4 — `KeyReusedWithDifferentPayload` throws before the delegate runs; actor is part of the record's composite key |
| `AC-101` | Missing/malformed/over-255-char key → `VALIDATION_FAILED` naming the field, before any mutation | §7.3 `AgentRequestGuard.RequireIdempotencyKey` runs first in every mutating operation |
| `AC-102` (CLI/MCP half) | REST, CLI, and MCP return identical symbolic values and a `correlationId` | §8.2 `AgentResponse<T>` adds `correlationId`; symbolic enum values already hold via `JsonStringEnumConverter`. REST half deferred to §16.2 |
| `AC-103` | A caller that omits `X-Correlation-Id` gets a server-generated `correlationId` matching an audit row | §9.4 — REST reads the header into `CorrelationContext`; agent generates per invocation |
| `AC-105` | Every response carries a non-empty `apiVersion` (`NFR-MNT-001`) | §8.2 — `AgentResponse<T>.ApiVersion` is non-nullable and set from `AgentContract.ApiVersion` |
| `AC-104` (regression guard) | MCP stdout carries only JSON-RPC frames | §13 — logging stays on stderr; no new `Console.WriteLine` on the MCP path |

### 3.7 Success Metrics

| Metric | Target |
|---|---|
| Agent operations reachable without authentication | **0** (asserted by a catalog-completeness test, not by inspection) |
| Agent operations without a declared permission | **0** (build-time-adjacent: a unit test over `OperationCatalog.Discover` fails the suite) |
| Mutating agent operations without an idempotency key | **0** |
| Agent responses without `apiVersion` | **0** |
| `IIdempotencyService` call sites | from **0** → ≥ 6 |
| Audit rows attributable to a specific credential | 100 % of agent-originated mutations |

## 4. System Context

```
        configuration / environment
     ANVILBOARD_AGENT__APITOKEN
                 │
                 ▼
   ┌──────────────────────────────────────────────────────────────┐
   │ Anvilboard.Agent (Program.cs)                                │
   │                                                              │
   │  CLI mode                     MCP mode                       │
   │  OperationCommandLineAdapter  McpOperationAdapter            │
   │            └────────┬────────────────┘                       │
   │                     ▼                                        │
   │            OperationInvoker(policies: [ ... ])   ◀── NEW     │
   │                     │                                        │
   │      ┌──────────────┴───────────────┐                        │
   │      ▼                              ▼                        │
   │  WorkspaceAuthorizationPolicy   AgentInvocationScope  ◀── NEW│
   │   authenticate → authorize       one DI scope per call       │
   │      │                              │                        │
   │      ▼                              ▼                        │
   │  IWorkspaceAuthorizationService  AgentActorAccessor  ◀── NEW │
   │                                     │                        │
   │                                     ▼                        │
   │                              BoardAgentService               │
   │                               │            │                 │
   │                   AgentIdempotency   AgentResponse<T> ◀── NEW│
   │                               │                              │
   └───────────────────────────────┼──────────────────────────────┘
                                   ▼
           IssueService · IssueLinkService · DashboardService · IBackupService
                                   │
                       IIdempotencyService · IAuditService
                                   ▼
                              AnvilboardDbContext (SQLite)
```

Everything below `BoardAgentService` is unchanged application code, called with better arguments.
Everything new lives inside `src/Anvilboard.Agent/` except one REST-side correlation fix.

## 5. Solution Design

### 5.1 Solution A (Recommended) — Declarative permissions enforced by one invocation policy, over a per-invocation DI scope

Mirror the REST host exactly, one layer down:

1. **Per-invocation scope.** Replace `ScopedServiceProvider`'s scope-per-`GetService` behaviour with
   an `AgentInvocationScope` — an `AsyncLocal<IServiceScope>` opened once per operation invocation.
   Every service the invocation touches (`IWorkspaceAuthorizationService`, `AgentActorAccessor`,
   `CorrelationContext`, `BoardAgentService`, `AnvilboardDbContext`) then shares one scope, exactly
   as ASP.NET Core gives one scope per HTTP request.
2. **Declarative permission metadata.** Annotate each operation method with
   `[RequiresAgentPermission(Permission.ReadWriteIssues, …)]`, the agent twin of the REST
   `RequiresPermissionAttribute`.
3. **One policy.** Register a `WorkspaceAuthorizationPolicy : IOperationInvocationPolicy` on the
   `OperationInvoker`. Per invocation it: opens the scope → resolves the configured credential →
   `AuthenticateAsync` → `AuthorizeAsync` against the declared permissions → stores the resulting
   `ActorContext` in the scoped `AgentActorAccessor` → `Allow()`, or `Deny(code)` on failure.
   An operation carrying **no** attribute is denied, so forgetting one fails closed.
4. **Actor propagation.** `BoardAgentService` reads `AgentActorAccessor.Actor` and passes real
   `MemberId`s where it currently passes `null`, and a credential-qualified actor string where it
   currently passes `"agent:automation"`.
5. **Idempotency + envelope in the service.** Mutating operations wrap their body in
   `AgentIdempotency.ExecuteAsync(...)` and return `AgentResponse<T>`.

**Pros:** one enforcement point, identical mental model to REST, impossible to add an unguarded
operation, uses the upstream package's designed seam with no forking. **Cons:** requires replacing
`ScopedServiceProvider`; the policy interface has no post-invocation hook, so idempotency `Commit`
must live in the service rather than the policy.

### 5.2 Solution B (Alternative) — Per-method authorization calls inside `BoardAgentService`

Each of the 13 methods begins with `var actor = await auth.AuthenticateAsync(...)` followed by
`await auth.AuthorizeAsync(actor, workspaceId, Permission.X, ct)`.

**Pros:** no DI-scope surgery; authorization and idempotency sit side by side in one method, so the
commit-after-success ordering is trivially visible. **Cons:** 13 hand-written call sites that a new
operation can silently omit; directly contradicts the `RequiresPermissionAttribute` docstring's
"single enforcement point" rule; duplicates authentication work per call; makes `BoardAgentService`
— explicitly documented as a *thin* wrapper — a security component.

### 5.3 Comparison Matrix

| Criterion | A — Policy + declarative metadata | B — Per-method calls |
|---|---|---|
| Fails closed on a new, unannotated operation | **Yes** (policy denies; a test asserts full coverage) | No — a new method is simply unguarded |
| Consistent with REST §11.2 single enforcement point | **Yes** | No |
| Lines of security-relevant code | ~1 policy + 13 attributes | 13 × ~6 lines, duplicated |
| Keeps `BoardAgentService` thin | **Yes** | No |
| DI changes required | Per-invocation scope (moderate) | None |
| Post-invocation hook for idempotency commit | Not available — handled in service | Natural |
| Auditability of the design itself | One file to review | 13 sites to re-review on every change |
| Upstream-package alignment | Uses the documented seam | Ignores it |

### 5.4 Decision & Rationale

**Solution A.** The deciding factor is the failure mode, not the line count. MAJ-015 exists precisely
because a mutation path was added without anyone remembering to guard it; Solution B reproduces the
conditions that caused the finding, while Solution A makes the omission impossible — an operation
with no `[RequiresAgentPermission]` is denied at runtime *and* fails a catalog-coverage unit test at
build time. The per-invocation scope is required work regardless: without it there is nowhere for an
`ActorContext` to live between the policy and the service, because today's `ScopedServiceProvider`
hands out a different scope on every single `GetService` call.

Solution A's one genuine weakness — no post-invocation hook for the idempotency commit — is
contained by keeping idempotency in the service where the result value is available anyway, and by
a shared `AgentIdempotency.ExecuteAsync` helper so the begin/execute/commit ordering is written once
(`DR-AGT-002`).

## 6. Architecture Design

```mermaid
sequenceDiagram
    participant Host as CLI / MCP adapter
    participant Inv as OperationInvoker
    participant Pol as WorkspaceAuthorizationPolicy
    participant Auth as IWorkspaceAuthorizationService
    participant Svc as BoardAgentService
    participant Idem as IIdempotencyService
    participant App as IssueService / IBackupService
    participant Audit as IAuditService

    Host->>Inv: InvokeAsync(operation, inputs, ctx)
    Inv->>Pol: EvaluateAsync(operation, inputs, ctx)
    Pol->>Pol: Open AgentInvocationScope (one DI scope)
    Pol->>Pol: Read [RequiresAgentPermission]; none -> Deny
    Pol->>Auth: AuthenticateAsync(ChannelCredential.FromApiToken)
    Auth-->>Pol: AuthenticationResult(Actor | ErrorCode)
    alt not authenticated
        Pol-->>Inv: Deny(AUTHENTICATION_REQUIRED | CREDENTIAL_INVALID_OR_EXPIRED)
        Inv-->>Host: failure (no service touched)
    else authenticated
        Pol->>Auth: AuthorizeAsync(actor, actor.WorkspaceId, permission)
        Auth->>Audit: RecordAuthorizationDecisionAsync(...)
        alt denied
            Pol-->>Inv: Deny(WORKSPACE_ACCESS_DENIED)
        else allowed
            Pol->>Pol: AgentActorAccessor.Set(actor)
            Pol-->>Inv: Allow()
            Inv->>Svc: bind args, resolve target from the same scope
            Svc->>Svc: AgentRequestGuard.RequireIdempotencyKey (mutations)
            Svc->>Idem: TryBeginAsync(workspace, actorId, op, key, hash)
            alt ReplayOriginal
                Idem-->>Svc: stored payload
                Svc-->>Host: AgentResponse(apiVersion, correlationId, replayed data)
            else KeyReusedWithDifferentPayload
                Svc-->>Host: IDEMPOTENCY_KEY_REUSED
            else New
                Svc->>App: mutate (real MemberId actor)
                App->>Audit: activity + audit with credential-qualified actor
                Svc->>Idem: CommitAsync(result payload, 30d retention)
                Svc-->>Host: AgentResponse(apiVersion, correlationId, data)
            end
        end
    end
```

**Ordering invariants** (each is a test in §15):

- Authentication precedes authorization precedes binding precedes any application call.
- Idempotency-key validation precedes `TryBeginAsync` precedes the mutation precedes `CommitAsync`.
- `CommitAsync` runs only after the wrapped mutation has committed — a failed mutation leaves no
  record, so a retry re-executes rather than replaying a failure.

## 7. Technology Stack & Conventions

### 7.1 Stack

No new dependencies. `dotnet-agent-surface` (already referenced), `Microsoft.Extensions.*`
(already referenced), `System.Security.Cryptography.SHA256` and `System.Text.Json` (BCL).
`.NET 10`, `Nullable` + `ImplicitUsings` enabled, matching every existing project.

### 7.2 Naming Conventions

| Concept | Name | Location |
|---|---|---|
| Permission metadata | `RequiresAgentPermissionAttribute` | `src/Anvilboard.Agent/Authorization/` |
| Invocation policy | `WorkspaceAuthorizationPolicy` | `src/Anvilboard.Agent/Authorization/` |
| Ambient scope holder | `AgentInvocationScope` | `src/Anvilboard.Agent/Hosting/` |
| Scoped actor carrier | `AgentActorAccessor` | `src/Anvilboard.Agent/Authorization/` |
| Credential resolution | `AgentCredentialSource`, `AgentOptions` | `src/Anvilboard.Agent/Hosting/` |
| Actor id formatting | `AgentActorId` | `src/Anvilboard.Agent/Authorization/` |
| Idempotency helper | `AgentIdempotency` | `src/Anvilboard.Agent/Automation/` |
| Canonical hashing | `CanonicalRequestHash` | `src/Anvilboard.Agent/Automation/` |
| Input guards | `AgentRequestGuard` | `src/Anvilboard.Agent/Automation/` |
| Response envelope | `AgentResponse<T>` | `src/Anvilboard.Agent/Contracts/` |
| Version constant | `AgentContract.ApiVersion` | `src/Anvilboard.Agent/Contracts/` |
| Failure signal | `AgentOperationException(string ErrorCode, string? Field)` | `src/Anvilboard.Agent/Contracts/` |

Configuration keys follow the existing `AddEnvironmentVariables("ANVILBOARD_")` prefix, so
`Agent:ApiToken` is supplied as `ANVILBOARD_AGENT__APITOKEN`.

### 7.3 Parameter Validation & Input Parsing

| Input | Rule | Failure |
|---|---|---|
| `Agent:ApiToken` | non-blank; trimmed | `AUTHENTICATION_REQUIRED` (never reveals whether the token is *wrong* vs *absent* beyond the standard codes) |
| `idempotencyKey` | required on mutating operations; non-blank; ≤ 255 chars; printable ASCII | `VALIDATION_FAILED` naming `idempotencyKey` (AC-101) |
| `title`, `body`, `type` | unchanged — already validated by the application services | unchanged |
| `workspaceId` on `create-backup` | **removed**; the workspace comes from `ActorContext.WorkspaceId` | n/a — the spoofing vector is deleted rather than validated |

`AgentRequestGuard.RequireIdempotencyKey(string? key)` is the first statement of every mutating
operation, ahead of any service call, so AC-101's "before any mutation executes" is structural.

### 7.4 Boundary Values & Edge Cases

| Case | Behaviour |
|---|---|
| Key of exactly 255 chars | accepted |
| Key of 256 chars | `VALIDATION_FAILED` |
| Empty / whitespace key | `VALIDATION_FAILED` |
| Same key, same payload, **different** token | treated as a different actor → `New`, because `ActorId` is part of the composite key (AC-008's "or actor") |
| Same key, same payload, same token, after 30 days | the record has expired → `New`; retention is documented in §10 and surfaced by `list`-style docs |
| Mutation succeeds, `CommitAsync` fails | the mutation stands; the retry re-executes. Accepted — see `DR-AGT-003` |
| Token revoked mid-MCP-session | the *next* invocation re-authenticates and fails closed; revocation is never deferred to a cache (matches the `RevokeCredentialAsync` docstring) |
| Token authenticates to workspace A, operation targets an entity in workspace B | the application services are already workspace-anchored; the policy additionally authorizes only against `actor.WorkspaceId`, and `create-backup` no longer accepts a workspace argument |
| Read operations | also require authentication (`ReadBoard` / `ReadDashboard`), so an unconfigured host cannot enumerate issues |

### 7.5 Business Logic Rules

1. An operation with no `[RequiresAgentPermission]` is **denied**, not allowed.
2. Holding **any one** of the declared permissions suffices (matching `RequiresPermissionAttribute`'s
   documented "any one of" semantics, which lets `ReadWriteIssues` or `ReadWriteAssignedIssues`
   satisfy the same operation).
3. The workspace is always `ActorContext.WorkspaceId`. No operation accepts a workspace from input.
4. Actor identity for audit/idempotency is `AgentActorId.For(actor)`:
   `member:{memberId}` normally, `member:{memberId}+token:{apiTokenId}` when the credential was an
   API token — satisfying MAJ-015's "attribute agent actions to a specific credential".
5. `AuditChannel` stays `Cli` / `Mcp` as derived today, but is derived once at host start and passed
   in, not re-read from `Environment.GetCommandLineArgs()` inside the service.
6. Idempotency applies to the 6 mutating operations only: `create-issue`, `change-issue-status`,
   `assign-issue`, `comment-on-issue`, `create-issue-link`, `remove-issue-link`. `create-backup` is
   excluded (`DR-AGT-005`).

### 7.6 Error Handling Strategy

The invoker converts a thrown exception into `OperationInvocationResult.Failure(message)`, and a
policy denial into the same shape. To keep `FR-AUT-003`'s "stable code, safe cause, correlation id"
promise on a channel that has no HTTP status, the failure **message** is a compact JSON document:

```json
{"apiVersion":"1.0","errorCode":"IDEMPOTENCY_KEY_REUSED","field":"idempotencyKey","correlationId":"…"}
```

`AgentOperationException` carries the code and optional field; a small translator renders it. Codes
come exclusively from `ErrorCodeCatalog` — no agent-specific codes are invented, so the CLI/MCP and
REST vocabularies stay identical (`AC-102`). The CLI exit code stays `1` for failures and `130` for
cancellation, as the upstream adapter already defines.

### 7.7 Error Catalog & Traceability

| Code | Raised when | Source |
|---|---|---|
| `AUTHENTICATION_REQUIRED` | no credential configured, or a malformed one | `AuthenticationResult.Failed` via policy |
| `CREDENTIAL_INVALID_OR_EXPIRED` | unknown, revoked, or expired token | `AuthenticateByApiTokenAsync` |
| `WORKSPACE_ACCESS_DENIED` | authenticated but lacking every declared permission, or operation missing metadata | `AuthorizationResult.Denied` via policy |
| `VALIDATION_FAILED` | missing/oversized/malformed `idempotencyKey` | `AgentRequestGuard` |
| `IDEMPOTENCY_KEY_REUSED` | key reused with a different canonical payload or actor | `AgentIdempotency` |
| `REFERENCED_ENTITY_NOT_FOUND`, `INVALID_WORKFLOW_TRANSITION`, `RESOURCE_ALREADY_EXISTS`, `CONCURRENCY_CONFLICT` | unchanged — surfaced from the application services | existing |

All six new-path codes already exist in
[`ErrorCodeCatalog`](../../src/Anvilboard.Application/Automation/ErrorCodeCatalog.cs); **no catalog
entry is added**, so no SRS Appendix A or tech-design §7.7 edit is required.

## 8. Detailed Design

### 8.1 Component Overview

| Component | Responsibility | Touches |
|---|---|---|
| `AgentOptions` / `AgentCredentialSource` | Bind `Agent:ApiToken`; produce a `ChannelCredential` or `null` | configuration |
| `AgentInvocationScope` | `AsyncLocal<IServiceScope>`; `Begin(root)` returns a disposable that restores the previous scope | DI |
| `ScopedServiceProvider` (rewritten) | Resolve from the ambient scope; fall back to a fresh scope only when none is active | DI |
| `RequiresAgentPermissionAttribute` | Declare required `Permission`s on an operation method | metadata |
| `WorkspaceAuthorizationPolicy` | Authenticate + authorize + publish the actor; deny otherwise | `IWorkspaceAuthorizationService` |
| `AgentActorAccessor` | Scoped carrier for the resolved `ActorContext` | scope |
| `AgentActorId` | Format a credential-qualified actor string | — |
| `AgentRequestGuard` | Validate `idempotencyKey` before any mutation | — |
| `CanonicalRequestHash` | Deterministic SHA-256 over a stable-ordered JSON projection of the arguments | — |
| `AgentIdempotency` | begin → execute → commit, with replay and reuse handling | `IIdempotencyService` |
| `AgentResponse<T>` / `AgentContract` | Envelope carrying `apiVersion`, `correlationId`, `data` | — |
| `BoardAgentService` (modified) | Declare permissions, take keys, pass real actors, return envelopes | all of the above |

### 8.2 Contracts

```csharp
// src/Anvilboard.Agent/Contracts/AgentContract.cs
public static class AgentContract
{
    /// <summary>The agent surface contract version (NFR-MNT-001, AC-105). Bumped only on a
    /// breaking change to operation inputs or the response envelope.</summary>
    public const string ApiVersion = "1.0";
}

// src/Anvilboard.Agent/Contracts/AgentResponse.cs
public sealed record AgentResponse<T>(string ApiVersion, string CorrelationId, T Data)
{
    public static AgentResponse<T> For(string correlationId, T data) =>
        new(AgentContract.ApiVersion, correlationId, data);
}

// src/Anvilboard.Agent/Authorization/RequiresAgentPermissionAttribute.cs
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresAgentPermissionAttribute(params Permission[] permissions) : Attribute
{
    public IReadOnlyList<Permission> Permissions { get; } = permissions;
}

// src/Anvilboard.Agent/Authorization/AgentActorAccessor.cs
public sealed class AgentActorAccessor
{
    public ActorContext Actor =>
        _actor ?? throw new InvalidOperationException(
            "No ActorContext resolved for this invocation; WorkspaceAuthorizationPolicy must run first.");

    internal void Set(ActorContext actor) => _actor = actor;
    private ActorContext? _actor;
}

// src/Anvilboard.Agent/Authorization/AgentActorId.cs
public static class AgentActorId
{
    public static string For(ActorContext actor) => actor.ApiTokenId is { } token
        ? $"member:{actor.MemberId.Value}+token:{token.Value}"
        : $"member:{actor.MemberId.Value}";
}
```

`AgentActorAccessor.Actor` throwing rather than returning `null` is deliberate: if the policy were
ever removed or reordered, every operation fails loudly instead of silently reverting to anonymous
behaviour.

### 8.3 Core Workflow — `WorkspaceAuthorizationPolicy.EvaluateAsync`

```
1. scope := AgentInvocationScope.Begin(rootProvider)         // one scope for this invocation
2. permissions := operation.Method.GetCustomAttribute<RequiresAgentPermissionAttribute>()
   if permissions is null or empty:
       return Deny(WORKSPACE_ACCESS_DENIED)                  // fail closed on an unannotated op
3. credential := AgentCredentialSource.Resolve()
   if credential is null:
       return Deny(AUTHENTICATION_REQUIRED)
4. auth := scope.GetRequiredService<IWorkspaceAuthorizationService>()
   result := await auth.AuthenticateAsync(credential, ct)
   if not result.IsAuthenticated:
       return Deny(result.ErrorCode)                         // AUTHENTICATION_REQUIRED | CREDENTIAL_INVALID_OR_EXPIRED
5. for each permission in permissions:                        // "any one of" semantics
       decision := await auth.AuthorizeAsync(actor, actor.WorkspaceId, permission, ct)
       if decision.IsAuthorized: break
   if none authorized:
       return Deny(WORKSPACE_ACCESS_DENIED)
6. scope.GetRequiredService<AgentActorAccessor>().Set(actor)
7. return Allow()
```

`AuthorizeAsync` already records the decision through `IAuditService`, so denials are audited with no
extra code — including the denial in step 5, which is the audit trail MAJ-015 asks for.

Scope lifetime: `Begin` pushes onto the `AsyncLocal` and the returned disposable is registered with
the host wrapper that owns the invocation (§9.1), which disposes it after the adapter returns. For
CLI mode the process exits immediately afterwards; for MCP mode each tool call gets and releases its
own scope, which also fixes the existing "scopes are intentionally never disposed" leak note in
`Program.cs`.

### 8.4 Core Workflow — `AgentIdempotency.ExecuteAsync`

```csharp
public async Task<T> ExecuteAsync<T>(
    string operation,
    string idempotencyKey,
    object canonicalRequest,
    Func<CancellationToken, Task<T>> mutation,
    CancellationToken ct)
{
    AgentRequestGuard.RequireIdempotencyKey(idempotencyKey);           // AC-101, before anything else

    var actor   = accessor.Actor;
    var actorId = AgentActorId.For(actor);
    var hash    = CanonicalRequestHash.Compute(canonicalRequest, jsonOptions);

    var begin = await idempotency.TryBeginAsync(
        actor.WorkspaceId, actorId, operation, idempotencyKey, hash, ct);

    switch (begin.Outcome)
    {
        case IdempotencyOutcome.ReplayOriginal:                        // AC-007
            return JsonSerializer.Deserialize<T>(begin.StoredResultPayload!, jsonOptions)!;

        case IdempotencyOutcome.KeyReusedWithDifferentPayload:         // AC-008
            throw new AgentOperationException("IDEMPOTENCY_KEY_REUSED", field: "idempotencyKey");
    }

    var result  = await mutation(ct);                                  // mutation commits here
    var payload = JsonSerializer.Serialize(result, jsonOptions);
    await idempotency.CommitAsync(
        actor.WorkspaceId, actorId, operation, idempotencyKey, hash, payload,
        IdempotencyService.DefaultRetention, ct);
    return result;
}
```

`CanonicalRequestHash.Compute` serializes an anonymous record of the operation's semantic inputs with
`JsonSerializerOptions` configured for a stable property order and no incidental whitespace, then
hashes with SHA-256 — matching the algorithm the feature spec already documents. The
`idempotencyKey` itself is **excluded** from the hash (it is the lookup key, not payload), as is the
`CancellationToken`.

The replay path deserializes the stored payload rather than re-running the mutation, so no
`ActivityEvent`, `AuditEvent`, or domain row is produced on a replay — which is precisely what
AC-007 asks a test to assert by row count.

### 8.5 `BoardAgentService` — before and after

```csharp
// before
[AgentOperation("create-issue", "Creates a new issue under a team", Category = "issues")]
public async Task<IssueSummary> CreateIssueAsync(
    Guid teamId, string title, string? description = null,
    IssuePriority priority = IssuePriority.None, Guid? assigneeId = null,
    CancellationToken cancellationToken = default)
{
    var issue = await issues.CreateAsync(
        new TeamId(teamId), title, description, priority, projectId: null,
        assigneeId is { } a ? new MemberId(a) : null, createdById: null, cancellationToken);
    return IssueSummary.FromIssue(issue);
}

// after
[AgentOperation("create-issue", "Creates a new issue under a team", Category = "issues",
    Examples = ["create-issue --teamId ... --title \"Forge the anvil\" --idempotencyKey 01J..."])]
[RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)]
public Task<AgentResponse<IssueSummary>> CreateIssueAsync(
    Guid teamId, string title, string idempotencyKey, string? description = null,
    IssuePriority priority = IssuePriority.None, Guid? assigneeId = null,
    CancellationToken cancellationToken = default) =>
    RespondAsync(idempotencyKey, "create-issue",
        new { teamId, title, description, priority, assigneeId },
        async ct =>
        {
            var issue = await issues.CreateAsync(
                new TeamId(teamId), title, description, priority, projectId: null,
                assigneeId is { } a ? new MemberId(a) : null,
                createdById: actors.Actor.MemberId, ct);      // real actor, was null
            return IssueSummary.FromIssue(issue);
        },
        cancellationToken);
```

`idempotencyKey` is a **required** (non-optional) parameter, which makes it a required property in
the MCP-generated input schema and a mandatory `--idempotencyKey` flag on the CLI — the contract is
visible to callers rather than discovered by failure. `AgentRequestGuard` still validates it so a
blank or oversized value yields `VALIDATION_FAILED` rather than a binding error.

Note the `Examples` rewrite in the snippet above is a **bug fix, not cosmetics**. All four
`Examples` strings currently on `BoardAgentService` (lines 71, 118, 133, 143) use `name=value`
syntax, but `OperationCommandLineAdapter.ParseInputs` requires `--name JSON-value` pairs and throws
`"Inputs must be supplied as --name JSON-value pairs."` on anything else — so every shipped example
is a command that cannot run. T9 corrects all four while touching these signatures.

Read operations gain only the attribute and the envelope:

```csharp
[AgentOperation("list-issues", "…", Category = "issues", IsIdempotent = true)]
[RequiresAgentPermission(Permission.ReadBoard)]
public async Task<AgentResponse<IReadOnlyList<IssueSummary>>> ListIssuesAsync(…) =>
    AgentResponse<IReadOnlyList<IssueSummary>>.For(correlation.CorrelationId, […]);
```

### 8.6 Permission map for the 13 operations

| Operation | Permission(s) | Idempotency key |
|---|---|---|
| `list-issues` | `ReadBoard` | — |
| `get-issue` | `ReadBoard` | — |
| `list-issue-links` | `ReadBoard` | — |
| `dashboard-summary` | `ReadDashboard` | — |
| `create-issue` | `ReadWriteIssues`, `ReadWriteAssignedIssues` | required |
| `change-issue-status` | `ReadWriteIssues`, `ReadWriteAssignedIssues` | required |
| `assign-issue` | `ReadWriteIssues`, `ReadWriteAssignedIssues` | required |
| `comment-on-issue` | `ReadWriteComments` | required |
| `create-issue-link` | `ReadWriteIssues`, `ReadWriteAssignedIssues` | required |
| `remove-issue-link` | `ReadWriteIssues`, `ReadWriteAssignedIssues` | required |
| `create-backup` | `ManageBackupRestore` | — (`DR-AGT-005`) |
| `list-backups` | `ManageBackupRestore` | — |
| `verify-backup` | `ManageBackupRestore` | — |

The default `AutomationAgent` role grants `ReadWriteIssues`, `ReadWriteComments`, `ReadBoard`,
`ReadDashboard` — so an out-of-the-box automation token can do issue work and **cannot** touch
backups, which is the intended outcome of scenario 2 in §3.5.

## 9. API Design

### 9.1 Host wiring

```csharp
var authPolicy = new WorkspaceAuthorizationPolicy(provider, credentialSource);
var invoker    = new OperationInvoker(new ScopedServiceProvider(), jsonOptions, [authPolicy]);
```

`ScopedServiceProvider` no longer needs the root provider for the normal path — it reads
`AgentInvocationScope.Current`. Both adapters are wrapped by a tiny `AgentInvocationHost` that owns
the scope's disposal:

- **CLI:** one invocation per process — `await using var _ = AgentInvocationScope.BeginOwned(provider)`
  around `adapter.ExecuteAsync(args)`.
- **MCP:** the `CallToolHandler` obtained from `McpOperationServer.CreateOptions()` is decorated so
  each tool call opens and disposes its own scope. `ListToolsHandler` is untouched.

### 9.2 CLI surface

```bash
# read
$env:ANVILBOARD_AGENT__APITOKEN = "…"
dotnet run -- issues list-issues --status '"InProgress"'

# mutation — --idempotencyKey is now required
dotnet run -- issues create-issue \
  --teamId '"…"' --title '"Forge the anvil"' --idempotencyKey '"01J8…"'
```

Output (stdout, `JsonAgentOutputRenderer`):

```json
{ "apiVersion": "1.0", "correlationId": "8f2a…", "data": { "id": "…", "key": "ANV-42", … } }
```

Failure (stderr, exit code 1):

```json
{ "apiVersion": "1.0", "errorCode": "WORKSPACE_ACCESS_DENIED", "correlationId": "8f2a…" }
```

The token is deliberately **not** accepted as a `--token` flag: command lines are visible in shell
history and the OS process list, and `OperationCommandLineAdapter.ParseInputs` rejects unknown flags
anyway, so a global flag would have to be stripped by the host before dispatch (`DR-AGT-001`).

### 9.3 MCP surface

Tool input schemas gain a required `idempotencyKey` string on the six mutating tools — generated
automatically from the method signature by `OperationSchemaGenerator`, so no schema code is written.
Tool results are the same JSON envelope as the CLI, satisfying AC-102's "identical symbolic values"
between the two agent channels. `ToolAnnotations.ReadOnlyHint` continues to reflect
`AgentSafetyLevel.Safe`; safety levels are unchanged by this plan (`DR-AGT-004`).

### 9.4 REST touchpoints (minimal)

The REST host **already** overrides the application-layer default and reads the inbound header, so
the ingest half of `AC-103` is done:

```csharp
// Anvilboard.Application/ServiceCollectionExtensions.cs — the channel-agnostic fallback
services.TryAddScoped(_ => CorrelationContext.FromHeaderOrNew(null));

// Anvilboard.Api/Program.cs — already present, wins over the TryAddScoped fallback
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(provider => CorrelationContext.FromHeaderOrNew(
    provider.GetRequiredService<IHttpContextAccessor>().HttpContext?
        .Request.Headers["X-Correlation-Id"]));
```

The one remaining REST change is to **echo** the resolved value back as an `X-Correlation-Id`
response header from `WorkspaceAuthorizationMiddleware` (via `OnStarting`, so it is set before the
body is flushed), so a caller that omitted the header can still learn the server-generated value
without a body-shape change. The agent host keeps the application-layer default, which has no
header and therefore always generates.

## 10. Data & Storage Design

**No schema change.** `IdempotencyRecords` already exists with the composite
`(WorkspaceId, ActorId, Operation, Key)` unique index and an `ExpiresAt` column, and
`IdempotencyService.DefaultRetention` already encodes the 30-day policy from tech-design §10.1 —
this plan is the first consumer.

| Table | Change | Notes |
|---|---|---|
| `IdempotencyRecords` | first writes | `ActorId` now carries the credential-qualified form from `AgentActorId.For` |
| `AuditEvents` | content only | `ActorId` moves from `"agent:automation"` to `member:…+token:…`; `Channel` stays `Cli`/`Mcp` |
| `ActivityEvents` | content only | `ActorId`/`AuthorId`/`CreatedById` move from `NULL` to a real `MemberId` |

**Expired-record cleanup** is not added here: `TryBeginAsync` currently matches on the composite key
without filtering `ExpiresAt`, so expiry is recorded but not yet enforced on read. That is a
pre-existing gap in `IdempotencyService`, and this plan fixes it in T7 by adding the `ExpiresAt >
now` predicate — the minimum needed for §7.4's "after 30 days → `New`" row to be true. A background
purge job remains out of scope (§16.2).

**Migration/compatibility:** existing `AuditEvents` rows keep the literal `agent:automation` actor.
No backfill: rewriting history would be worse than a documented cutover point, and the audit trail's
value is forward-looking.

## 11. Security Design

### 11.1 Authentication

The agent host authenticates with a workspace-scoped API token resolved from configuration. It uses
`ChannelCredential.FromApiToken`, the same input the REST host uses for its `Authorization` header
path, so token hashing, revocation, and expiry checks are shared verbatim — there is no second
credential code path to review. Username/password is deliberately not supported on this channel
(it would put a human password in an environment variable).

### 11.2 Authorization

Single enforcement point, as on REST: `WorkspaceAuthorizationPolicy` is the only place in
`Anvilboard.Agent` permitted to call `AuthorizeAsync`, and `BoardAgentService` is forbidden from
calling either auth method — enforced by a unit test asserting the agent assembly has no
`IWorkspaceAuthorizationService` reference outside `Authorization/`.

Fail-closed properties:

- No attribute → denied.
- No credential → denied.
- Authenticated but unpermitted → denied, audited.
- `AgentActorAccessor.Actor` throws if the policy did not run.

### 11.3 Credential handling

`ChannelCredential` is never logged, never persisted, and never placed in `OperationInvocationContext`
(the `Credential` slot stays unused — a hash-or-nothing rule; the accessor carries the resolved
`ActorContext`, which by its own docstring holds "only non-secret identifiers"). The token never
appears in an error message: an invalid token yields the same `CREDENTIAL_INVALID_OR_EXPIRED` as an
unknown one, matching `AuthenticateAsync`'s non-disclosure contract.

### 11.4 Audit logging

Every agent mutation now produces an audit trail that names the credential:

| Field | Value |
|---|---|
| `ActorId` | `member:{memberId}+token:{apiTokenId}` |
| `Channel` | `AuditChannel.Cli` or `AuditChannel.Mcp` |
| `CorrelationId` | the invocation's correlation id, also returned in the envelope |
| authorization decisions | emitted by `AuthorizeAsync` for both grants and denials |

This is the concrete closure condition for MAJ-015's *"audit trails cannot attribute agent actions to
a specific credential"*.

### 11.5 Threat model deltas

| Threat | Before | After |
|---|---|---|
| Local process reads/writes any workspace via CLI | possible (no auth) | requires a valid token for that workspace |
| Agent triggers a backup of an arbitrary workspace | possible (`workspaceId` is an argument) | parameter removed; workspace is the token's |
| Agent action untraceable to a principal | yes | no |
| Duplicate mutations from retries | unbounded | bounded by the idempotency record |
| Privilege escalation via a newly added operation | silent | denied by default |

## 12. Performance Design

Per invocation this adds: one token-hash lookup (`ApiTokens.TokenHash` is uniquely indexed), one
`Members` lookup by primary key, one audit insert (already the case on REST), and — for mutations —
one indexed `IdempotencyRecords` lookup plus one insert. All are single-row SQLite operations against
indexed columns; expected added latency is sub-millisecond and dwarfed by the process start-up and
`Database.MigrateAsync()` the CLI already performs.

The per-invocation scope is a net **improvement** over today's scope-per-`GetService`, which creates
(and leaks) one `AnvilboardDbContext` per resolved dependency. In MCP mode, where the process is
long-lived, this converts an unbounded scope leak into bounded, disposed-per-call scopes.

`CanonicalRequestHash` serializes a small anonymous record and hashes a few hundred bytes — negligible.

## 13. Observability

| Signal | Where |
|---|---|
| `correlationId` | every success envelope, every error document, and the matching `AuditEvents` row |
| authorization grant/denial | `AuditEvents` via `RecordAuthorizationDecisionAsync` |
| idempotent replay | a structured **stderr** log line at `Information` (`operation`, `key`, `outcome=replay`) — never stdout |
| credential attribution | `AuditEvents.ActorId` |
| contract version | `apiVersion` on every response |

Logging configuration is unchanged: [`Program.cs`](../../src/Anvilboard.Agent/Program.cs) already
pins the console logger to stderr specifically so MCP's stdout stays protocol-only, and no new
`Console.WriteLine` is introduced on the MCP path (`AC-104` regression guard in §15).

## 14. Deployment & Rollback

**Breaking change, deliberately.** Existing CLI/MCP invocations break in three ways: they now need a
token, mutating calls now need `--idempotencyKey`, and the result shape gains an envelope. There is
no "compatibility mode" flag — an opt-out switch for an authorization fix reintroduces exactly the
hole being closed (`DR-AGT-006`).

Rollout:

1. Mint an automation token: `POST /api/auth/credentials` with the `AutomationAgent` role and the
   minimum permission grant for the workload.
2. Set `ANVILBOARD_AGENT__APITOKEN` in the MCP host / shell profile.
3. Update any script to pass `--idempotencyKey` and to read `.data` from the envelope.

Documented in [`README.md`](../../README.md) "Using the agent surface" and
[`DEVELOPMENT.md`](../../DEVELOPMENT.md) as part of T13.

**Rollback** is a revert of the agent project plus the one REST correlation change; there is no
migration and no data shape to undo. Rows already written to `IdempotencyRecords` become inert.

## 15. Testing Strategy

| Layer | Project | Coverage |
|---|---|---|
| Unit — catalog invariants | `Anvilboard.Agent.Tests/Authorization/OperationCoverageTests.cs` | Every operation in `OperationCatalog.Discover(typeof(BoardAgentService))` carries a `[RequiresAgentPermission]` with ≥ 1 permission (the guard that makes G2 structural); every mutating operation declares a required `idempotencyKey` parameter; every operation's declared return type is `AgentResponse<>` (AC-105 by construction) |
| Unit — policy | `Anvilboard.Agent.Tests/Authorization/WorkspaceAuthorizationPolicyTests.cs` | No credential → `AUTHENTICATION_REQUIRED`; bad token → `CREDENTIAL_INVALID_OR_EXPIRED`; authenticated-but-unpermitted → `WORKSPACE_ACCESS_DENIED`; unannotated operation → denied; "any one of" semantics; actor published to the accessor on success; **no application service is touched on any denial path** (asserted with a throwing fake) |
| Unit — guards & hashing | `Anvilboard.Agent.Tests/Automation/AgentRequestGuardTests.cs`, `CanonicalRequestHashTests.cs` | AC-101 boundaries (blank, 255, 256, non-printable); hash stability across property order and across process runs; key excluded from the hash; different argument → different hash |
| Unit — idempotency helper | `Anvilboard.Agent.Tests/Automation/AgentIdempotencyTests.cs` | AC-007 replay does not invoke the delegate; AC-008 reuse throws before the delegate; `New` path commits exactly once **after** the delegate; a throwing delegate commits nothing; different token → different `ActorId` → `New` |
| Unit — scope | `Anvilboard.Agent.Tests/Hosting/AgentInvocationScopeTests.cs` | One scope per invocation; the same `AnvilboardDbContext` instance is resolved twice within one invocation; scope disposed after the invocation; nested/sequential invocations do not bleed actors |
| Integration — agent end to end | `Anvilboard.Agent.Tests/BoardAgentServiceIntegrationTests.cs` | Against a temp SQLite file + a bootstrapped workspace: read op with a valid token; mutation writes a real `CreatedById`; AC-007 asserted **by row count** on `Issues`/`ActivityEvents`/`AuditEvents` across a replay; AC-008; `create-backup` denied for an issue-only token; audit row carries `member:…+token:…` and the envelope's `correlationId` |
| Integration — REST correlation | `Anvilboard.Api.Tests/Authorization/CorrelationIdTests.cs` | AC-103: request with `X-Correlation-Id` echoes it and the `AuditEvents` row matches; request without it gets a non-empty server-generated id |
| Regression — MCP stdout | `Anvilboard.Agent.Tests/Hosting/McpStdoutTests.cs` | AC-104: a denied and an allowed invocation both leave stdout free of non-JSON-RPC output (captured `Console.Out`) |
| Unchanged | `Anvilboard.Agent.Tests/BoardAgentServiceTests.cs` | The 13-name list is unchanged; `Discover_DoesNotExposeRestore`'s rationale comment is rewritten (T12) to cite `DR-AGT-004` rather than the now-closed MAJ-015 |

**Fixtures needed:** a bootstrapped workspace with (a) an `AutomationAgent` member holding an API
token with default grants, (b) a second token whose `GrantedPermissions` exclude
`ManageBackupRestore`, (c) an expired token, (d) a revoked token, (e) a member in a *second*
workspace, and (f) a pre-seeded `IdempotencyRecord` for the replay and reuse paths.

`Anvilboard.Agent.Tests` currently references only `Anvilboard.Agent` and has no database harness. It
gains `ProjectReference`s to `Anvilboard.Infrastructure` (for `AnvilboardDbContext`) and a small
`AgentFactory` test helper mirroring
[`ApiFactory`](../../src/Anvilboard.Api.Tests/Testing/ApiFactory.cs)'s temp-SQLite-path and
pool-clearing behaviour — the only test-infrastructure addition this plan requires.

**Command:** `dotnet test Anvilboard.slnx`

## 16. Milestones & Task Breakdown

Dependency-ordered. Total ≈ 71 h, inside the §16 **M4** two-week allowance.

| # | Task | Files | Depends on | Est. |
|---|---|---|---|---|
| T1 | `AgentOptions` + `AgentCredentialSource` (bind `Agent:ApiToken`, produce `ChannelCredential`) | `src/Anvilboard.Agent/Hosting/` | — | 2 h |
| T2 | `AgentInvocationScope` + rewritten `ScopedServiceProvider` (ambient scope, proper disposal) | `src/Anvilboard.Agent/Hosting/`, `src/Anvilboard.Agent/Program.cs` | — | 5 h |
| T3 | `RequiresAgentPermissionAttribute`, `AgentActorAccessor`, `AgentActorId` | `src/Anvilboard.Agent/Authorization/` | — | 2 h |
| T4 | `WorkspaceAuthorizationPolicy` (§8.3) + DI registration + `OperationInvoker` policy wiring | `src/Anvilboard.Agent/Authorization/`, `Program.cs` | T1, T2, T3 | 6 h |
| T5 | `AgentContract`, `AgentResponse<T>`, `AgentOperationException` + failure-document translator | `src/Anvilboard.Agent/Contracts/` | — | 3 h |
| T6 | `AgentRequestGuard` + `CanonicalRequestHash` | `src/Anvilboard.Agent/Automation/` | T5 | 3 h |
| T7 | `AgentIdempotency.ExecuteAsync` (§8.4) + `ExpiresAt` predicate in `IdempotencyService.TryBeginAsync` | `src/Anvilboard.Agent/Automation/`, `src/Anvilboard.Application/Automation/IdempotencyService.cs` | T3, T6 | 5 h |
| T8 | `BoardAgentService` — annotate all 13 ops, thread the real actor, drop `AutomationActorId` and the `create-backup` `workspaceId` parameter | `src/Anvilboard.Agent/BoardAgentService.cs` | T4 | 6 h |
| T9 | `BoardAgentService` — required `idempotencyKey` on the 6 mutations, `AgentResponse<T>` on all 13, and correct the 4 invalid `Examples` strings to `--name value` syntax | same | T7, T8 | 5 h |
| T10 | MCP `CallToolHandler` scope decoration + CLI scope ownership (`AgentInvocationHost`) | `src/Anvilboard.Agent/Hosting/`, `Program.cs` | T2, T4 | 4 h |
| T11 | Agent unit tests — catalog invariants, policy, guards/hashing, idempotency, scope | `src/Anvilboard.Agent.Tests/` | T9, T10 | 10 h |
| T12 | `AgentFactory` test harness + agent integration tests (AC-007/008 by row count, denial paths, audit attribution) + rewrite `Discover_DoesNotExposeRestore`'s rationale | `src/Anvilboard.Agent.Tests/` | T11 | 10 h |
| T13 | REST `X-Correlation-Id` response-header echo (ingestion already exists) + `Anvilboard.Api.Tests` coverage (AC-103) | `src/Anvilboard.Api/`, `src/Anvilboard.Api.Tests/` | — | 2 h |
| T14 | MCP stdout regression test (AC-104) | `src/Anvilboard.Agent.Tests/Hosting/` | T10 | 2 h |
| T15 | Operator docs — token setup, `--idempotencyKey`, envelope shape, breaking-change note | `README.md`, `DEVELOPMENT.md` | T12 | 3 h |
| T16 | Canonical doc updates (see §16.1) incl. correcting the stale MAJ-002 | `docs/**`, `CHANGELOG.md` | T15 | 3 h |

**Critical path:** T2 → T4 → T8 → T9 → T11 → T12 → T15 → T16.

### 16.1 Canonical documents to update on completion

Doc-first discipline: these are edited **in place**; no parallel or `-v2` files, and all existing
`FR-*`/`NFR-*`/`AC-*`/`MAJ-*` IDs are preserved.

| Document | Edit |
|---|---|
| [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md) | Status row: strike the "do not enforce workspace authorization/actor identity, idempotency" clause; document the `apiVersion`/`correlationId` envelope, the required `idempotencyKey`, and the credential configuration key; update the "Correlation ID Propagation" bullet now that REST really does read `X-Correlation-Id` |
| [`docs/features/overview.md`](../features/overview.md) | Row 5 status and the note in the sequencing section about the automation surface's authorization gap |
| [`docs/anvilboard/tech-design.md`](../anvilboard/tech-design.md) | §16 **M4** row: remove "`IdempotencyRecord` exists but is not wired through the agent surface"; note the residual REST-envelope work so the row stays honest rather than flipping to Implemented |
| [`docs/audit-report.md`](../audit-report.md) | **MAJ-015**, **MAJ-016**, **MAJ-017** marked `RESOLVED` with evidence, in the same style as the existing CRIT-002 block; **MAJ-001** resolved (REST already enforced; CLI/MCP now does); **MAJ-002 corrected — it was already resolved before this plan** (`RevokeCredentialAsync` + `DELETE /api/auth/credentials/{id:guid}` both exist); priority actions **#4** and **#6** struck through |
| [`docs/plans/backup-and-restore.md`](./backup-and-restore.md) | §17 `OQ-P3`: record that MAJ-015 has closed and that agent-side `restore` stays excluded for the new reason in `DR-AGT-004` |
| [`README.md`](../../README.md), [`DEVELOPMENT.md`](../../DEVELOPMENT.md) | "Using the agent surface": token configuration, `--idempotencyKey`, envelope output, breaking-change note |
| [`CHANGELOG.md`](../../CHANGELOG.md) | Breaking-change entry for the agent surface |

### 16.2 Deliberate follow-ups (not this plan)

| Item | Why deferred |
|---|---|
| REST response envelope (`apiVersion` + `data` wrapper) | Completes `AC-102`/`AC-105` on the third channel, but requires migrating every endpoint **and** the Angular client — a separate contract migration with its own rollout |
| REST idempotency (`Idempotency-Key` header) | `FR-AUT-002` applies to REST too; the helper built in T7 is channel-agnostic and can be lifted into `Anvilboard.Application` when that work starts |
| `/api` → `/api/v1` prefix | Pre-existing, cross-cutting; already recorded as `OQ-P1` in [`backup-and-restore.md`](./backup-and-restore.md) §17 |
| Expired-`IdempotencyRecord` purge job | T7 makes expiry *correct on read*; reclaiming rows is a maintenance concern with no correctness impact |
| Agent-side `restore` with a confirmation policy | See `DR-AGT-004` |
| `FR-AUT-001` pagination/filtering/ordering contract | Untouched by this plan; belongs with the REST contract migration |

## 17. Open Questions & Decision Records

| ID | Question / Decision | Status | Resolution |
|---|---|---|---|
| `DR-AGT-001` | Supply the token via a `--token` CLI flag or configuration only? | Resolved | **Configuration/environment only** (`ANVILBOARD_AGENT__APITOKEN`). A command-line token leaks into shell history and the OS process list, and `OperationCommandLineAdapter.ParseInputs` rejects unknown flags, so a global flag would additionally require host-side argument stripping. |
| `DR-AGT-002` | Enforce idempotency in the policy or in the service? | Resolved | **In the service**, via one shared `AgentIdempotency.ExecuteAsync`. `IOperationInvocationPolicy` has no post-invocation hook and no access to the result value, so a policy could begin but never commit. |
| `DR-AGT-003` | Mutation succeeds but `CommitAsync` fails — compensate? | Resolved | **No.** The retry re-executes, which is the same outcome as a network failure before the response reached the caller. Compensating would require a distributed transaction across the mutation and the record for a case a caller cannot distinguish anyway. |
| `DR-AGT-004` | Now that authorization exists, expose `restore` on the agent surface? | Resolved | **No, not in this plan.** Authorization is necessary but not sufficient: `restore` is instance-wide and destructive, so it needs `AgentSafetyLevel.Dangerous` plus an `IConfirmationEnforcingPolicy`, and MCP has no interactive confirmation channel — an MCP client could set the confirmed flag itself. Exposing it needs a designed confirmation UX, not just a permission check. `Discover_DoesNotExposeRestore` stays, with its rationale updated from MAJ-015 to this record. |
| `DR-AGT-005` | Require an idempotency key on `create-backup`? | Resolved | **No.** A backup is a non-destructive snapshot whose duplicate is a wasted file, not a corrupted state; `IBackupService` already de-duplicates concurrent runs through the restore coordinator. Keys are reserved for operations that mutate workspace data. |
| `DR-AGT-006` | Ship a compatibility flag that keeps the surface unauthenticated? | Resolved | **No.** An opt-out for an authorization fix reintroduces the exact hole MAJ-015 records, and would have to be defended in every future review. The break is documented instead. |
| `DR-AGT-007` | Add a request-side `apiVersion` input for negotiation, as MAJ-017's fix text suggests? | Resolved | **Response envelope + constant only, for now.** With exactly one version defined, a required input whose only legal value is `"1.0"` adds friction and no compatibility signal. The envelope gives clients the branch point they need; a request-side field is added with v2, when there is something to negotiate. Recorded here so the deviation from MAJ-017's literal wording is deliberate and reviewable. |
| `OQ-AGT-008` | Should a token be pinnable to a specific team as well as a workspace? | Open | Deferred. `ActorContext` has no team scope, and adding one affects REST equally. Revisit if multi-team workspaces produce a real least-privilege complaint. |

## 18. Appendix

### A. Glossary

| Term | Meaning |
|---|---|
| Agent surface | The CLI + MCP host in `src/Anvilboard.Agent`, built on `dotnet-agent-surface` |
| Operation | An `[AgentOperation]`-annotated method discovered by `OperationCatalog.Discover` |
| Invocation policy | An `IOperationInvocationPolicy` run by `OperationInvoker` before every invocation |
| Invocation scope | The single DI scope shared by everything participating in one operation invocation |
| Canonical request hash | SHA-256 over a deterministically serialized projection of an operation's semantic inputs |
| Credential-qualified actor | `member:{id}+token:{id}` — the attributable identity MAJ-015 requires |
| Envelope | `AgentResponse<T>` = `{ apiVersion, correlationId, data }` |

### B. References

- [`dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface) — `OperationInvoker`, `OperationInvocationPolicy`, `OperationCommandLineAdapter`, `McpOperationAdapter`
- Model Context Protocol — stdio transport and tool-annotation semantics

### C. Related Documents

- [`docs/features/agent-and-automation-surface.md`](../features/agent-and-automation-surface.md) — canonical feature spec
- [`docs/features/workspace-authorization.md`](../features/workspace-authorization.md) — the enforcement model this plan extends to a second channel
- [`docs/plans/backup-and-restore.md`](./backup-and-restore.md) — §9.3 and `OQ-P3`, the blocked `restore` exposure
- [`docs/plans/artifact-service.md`](./artifact-service.md) — plan-format precedent and the `DR-*` decision-record convention
- [`docs/audit-report.md`](../audit-report.md) — MAJ-001, MAJ-002, MAJ-015, MAJ-016, MAJ-017

### D. Requirements Traceability

| Requirement | Criterion | Satisfied by | Verified by |
|---|---|---|---|
| `FR-AUT-002` (1) | Same actor + key + equivalent request replays the original result, no duplicate side effects | §8.4 replay path | `AgentIdempotencyTests`, `BoardAgentServiceIntegrationTests` (row counts) |
| `FR-AUT-002` (2) | Key reuse with a different request → `IDEMPOTENCY_KEY_REUSED` | §8.4 reuse path | `AgentIdempotencyTests`, integration negative test |
| `FR-AUT-002` (3) | The result identifies the correlation id | §8.2 envelope | integration test matching envelope ↔ `AuditEvents` |
| `FR-AUT-002` (4) | Retention duration documented and observable | §10 + `IdempotencyService.DefaultRetention` + T7 expiry predicate | `AgentIdempotencyTests` expiry case |
| `NFR-MNT-001` / `AC-105` | Every response carries a non-empty `apiVersion` | §8.2 non-nullable envelope field | `OperationCoverageTests` (return type is `AgentResponse<>`) |
| `FR-AUT-001` (3) | Response includes a schema/API version and correlation id | §8.2 (CLI/MCP); REST deferred §16.2 | `OperationCoverageTests`, integration |
| `FR-AUT-001` (4) / `AC-104` | MCP stdout is protocol-only | §13 | `McpStdoutTests` |
| `FR-AUT-003` | Machine-readable errors with a stable code and correlation id | §7.6, §7.7 | policy + guard unit tests |
| `NFR-SEC-001` / `NFR-SEC-002` | Workspace-scoped authorization and non-disclosing failures on every channel | §8.3, §11 | `WorkspaceAuthorizationPolicyTests` |
| `AC-103` | Server-generated `correlationId` when the header is omitted | §9.4 | `CorrelationIdTests` |
