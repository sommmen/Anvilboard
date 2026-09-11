# Real-time Updates — Implementation Plan

> Sequenced, codebase-grounded build plan for [`realtime-updates.md`](./realtime-updates.md).
> Source specs: `docs/anvilboard/srs.md` FR-WRK-014, FR-INT-006, NFR-PERF-002;
> `docs/anvilboard/tech-design.md` §8.1, §9, §12, §16 (M6.5).
> Created: 2026-09-05

| Field | Value |
|-------|-------|
| Component | realtime-updates |
| Priority | P1 |
| Milestone | `tech-design.md` §16 **M6.5: Real-Time Dashboard & Plugin Events** (2 weeks) |
| Feature spec | [`realtime-updates.md`](./realtime-updates.md) |
| SRS Refs | `FR-WRK-014`, `FR-INT-006`, `NFR-PERF-002` |
| Acceptance criteria | `AC-RT-001`–`AC-RT-006` |
| Audit finding | [`docs/audit-report.md`](../audit-report.md) `CRIT-002` |
| Depends On | workspace-authorization (Partial), issue-board-service (Partial) |

## 1. Why this feature

Real-time Updates is the only component in [`overview.md`](./overview.md) whose Status is
**Not Started** rather than *Partial*, and it is P1. [`docs/audit-report.md`](../audit-report.md)
`CRIT-002` records the evidence: no `Hub`, SignalR, or WebSocket type exists anywhere in `src/`,
and `src/anvilboard-web` has no realtime client. Its two declared dependencies —
workspace-authorization and issue-board-service — are already implemented far enough to build
against (`IWorkspaceAuthorizationService`, `ActorContext`, `IssueService`), so the work is
actionable now rather than blocked.

The plan below turns [`realtime-updates.md`](./realtime-updates.md) into a dependency-ordered
sequence of changes against the code that exists today.

## 2. Verified current state

| Claim | Evidence |
|---|---|
| No realtime code exists | No match for `SignalR`, `IRealtimeUpdatePublisher`, or `RealtimeChange` anywhere under `src/`. |
| No realtime client | `src/anvilboard-web/package.json` has no `@microsoft/signalr` dependency and no socket/event-stream service exists under `src/anvilboard-web/src/app/`. |
| Web board refreshes by full re-fetch | `BoardPage.refresh()` in `src/anvilboard-web/src/app/board/board-page/board-page.ts` calls `api.listIssues()` and replaces the whole `issues` signal. |
| A single post-commit fan-out point already exists | `IssueService.RecordAndDispatchAsync` (`src/Anvilboard.Application/Issues/IssueService.cs`) writes the `ActivityEvent`, calls `SaveChangesAsync`, then fires `IIssueHook` plugins best-effort via `Task.WhenAll(...)`. |
| Authorization has one enforcement point | `WorkspaceAuthorizationMiddleware` (`src/Anvilboard.Api/Authorization/`) authenticates every request and authorizes it against `RequiresPermissionAttribute` metadata; endpoints never check themselves. |
| The API host exposes `Program` for tests | `public partial class Program;` at the end of `src/Anvilboard.Api/Program.cs`; `src/Anvilboard.Api.Tests` already has a working `ApiFactory : WebApplicationFactory<Program>` with a per-test temp SQLite path. |
| `tests/Anvilboard.IntegrationTests` is empty | The project contains only a `.csproj`; it references Application + Infrastructure but **not** `Anvilboard.Api`. Noted as a gap in `docs/anvilboard/test-cases.md` §6. |

### 2.1 Spec path reconciliation

[`realtime-updates.md`](./realtime-updates.md)'s *File Structure* and *Test Module* sections name
paths that do not match this repository. The plan uses the real paths and treats the spec's paths
as the intent, not the literal target:

| Spec path | Actual target | Why |
|---|---|---|
| `src/Anvilboard.Web/Features/Board/realtimeBoardSync.ts` | `src/anvilboard-web/src/app/core/realtime-board-sync.service.ts` | The Angular project is `src/anvilboard-web`; it uses Angular's standard `src/app/<area>/` layout and kebab-case filenames (`board-api.service.ts`). |
| `tests/Anvilboard.Web.Tests/board/realtimeBoardSync.test.ts` | `src/anvilboard-web/src/app/core/realtime-board-sync.service.spec.ts` | Web tests are colocated with sources and run through `ng test` (`@angular/build:unit-test` + vitest), as `src/anvilboard-web/src/app/app.spec.ts` shows. |
| `tests/Anvilboard.IntegrationTests/Realtime/*.cs` | `src/Anvilboard.Api.Tests/Realtime/*.cs` | Hub connection/authorization tests need the API host. `tests/Anvilboard.IntegrationTests` has no `Anvilboard.Api` project reference, while `src/Anvilboard.Api.Tests` already has `Microsoft.AspNetCore.Mvc.Testing` and a working factory. Publisher-only tests that need no host go to `src/Anvilboard.Application.Tests/Realtime/`. |

`src/Anvilboard.Application/Realtime/` and `src/Anvilboard.Infrastructure/Realtime/` match the
repository layout as written and are used unchanged.

## 3. Design decisions taken by this plan

These resolve ambiguities in the feature spec before implementation starts. Each is a deliberate,
reversible choice; changing one changes only the phase that owns it.

| # | Decision | Rationale |
|---|---|---|
| D1 | `IRealtimeUpdatePublisher` lives in `Anvilboard.Application`; the SignalR implementation lives in `Anvilboard.Infrastructure`. | The spec's constraint "must not depend on a web-controller type". `Anvilboard.Application` already references `Anvilboard.Infrastructure` (not the reverse), so the interface must sit in Application and the implementation in Infrastructure to avoid a cycle. |
| D2 | A `NullRealtimeUpdatePublisher` is the default registration in `AddAnvilboardApplication()`; the SignalR publisher replaces it in the API host only. | `Anvilboard.Agent` (CLI/MCP) shares `AddAnvilboardApplication()` but has no hub. This mirrors the existing `AddAnvilboardSyncCoordinator()` split, which exists for exactly this reason. |
| D3 | `RealtimeIssueChange.Version` is declared `long`, and the publisher widens `Issue.Version` (`int`) at the call site. | The spec says `long`; `src/Anvilboard.Domain/Issue.cs` declares `public int Version`. Widening at the boundary keeps the wire schema forward-compatible without a domain migration. No domain change is in scope here. |
| D4 | The workspace for an issue change is resolved via `Team.WorkspaceId`. | `Issue` carries `TeamId`, not `WorkspaceId` (`src/Anvilboard.Domain/Issue.cs`, `Team.cs`). `ChangeStatusAsync` already loads the team; the other mutation paths need the same lookup added. |
| D5 | Hub authorization calls `IWorkspaceAuthorizationService` explicitly inside the hub, not via `WorkspaceAuthorizationMiddleware`. | SignalR negotiation and the persistent connection do not flow through per-request endpoint metadata the way `RequiresPermissionAttribute` does. Reusing the same *service* preserves §11.2's "single enforcement point" intent without pretending the middleware covers it. |
| D6 | `IPluginEventPublisher` (`FR-INT-006` / `AC-RT-006`) is Phase 7 and explicitly optional for the first release of this component. | The interface does not exist in `src/Anvilboard.Plugins.Abstractions/` yet; it is owned by [`integration-and-plugin-platform.md`](./integration-and-plugin-platform.md) (AC-IPP-113). Phases 1–6 deliver `FR-WRK-014` and `NFR-PERF-002` standalone. |
| D7 | Coalescing runs in a single hosted background dispatcher with a bounded channel, not per-connection. | The spec requires bounded work and drop/coalesce policy; one bounded `Channel<RealtimeChange>` plus a per-`(workspaceId, issueId)` latest-wins map is the smallest thing that satisfies AC-RT-003 and AC-RT-004 together. |

## 4. Phased task breakdown

Phases are dependency-ordered. Each phase is independently buildable and testable; a phase's tests
should pass before the next begins.

### Phase 1 — Transport-neutral contracts

**Goal:** the change envelopes and the publisher seam exist, with a no-op default, and nothing
else in the system behaves differently yet.

| Task | Files |
|---|---|
| 1.1 Add `IRealtimeUpdatePublisher` with `ValueTask PublishAsync(RealtimeChange, CancellationToken = default)`. | **create** `src/Anvilboard.Application/Realtime/IRealtimeUpdatePublisher.cs` |
| 1.2 Add the `RealtimeChange` hierarchy: abstract base plus `RealtimeIssueChange`, `RealtimeActivityChange`, `RealtimeDashboardChange`, and the `RealtimeIssueChangeKind` enum. Use `long Version` per D3. | **create** `src/Anvilboard.Application/Realtime/RealtimeChange.cs` |
| 1.3 Add `NullRealtimeUpdatePublisher` returning `ValueTask.CompletedTask`. | **create** `src/Anvilboard.Application/Realtime/NullRealtimeUpdatePublisher.cs` |
| 1.4 Register the null publisher with `TryAddSingleton` so a host can override it. | **modify** `src/Anvilboard.Application/ServiceCollectionExtensions.cs` (`AddAnvilboardApplication`) |

`TryAddSingleton` (not `AddSingleton`) matters: the API host registers the SignalR publisher
*before* calling `AddAnvilboardApplication()`, or overrides after — either way the null publisher
must not win. The file already imports `Microsoft.Extensions.DependencyInjection.Extensions` for
the existing `TryAddScoped<IAuditService, …>` call.

**Verification:** `dotnet build`; no behavior change. No new tests required beyond compilation.

### Phase 2 — Post-commit publication from the mutation path

**Goal:** every committed issue mutation emits exactly one versioned, workspace-scoped change, and
a failing publisher cannot affect the mutation.

| Task | Files |
|---|---|
| 2.1 Inject `IRealtimeUpdatePublisher` into `IssueService`'s primary constructor. | **modify** `src/Anvilboard.Application/Issues/IssueService.cs` |
| 2.2 Extend `RecordAndDispatchAsync` to take the change kind, and after `SaveChangesAsync` publish a `RealtimeIssueChange` and a `RealtimeActivityChange` alongside the existing hook fan-out. | **modify** `src/Anvilboard.Application/Issues/IssueService.cs` |
| 2.3 Resolve `WorkspaceId` via `Team.WorkspaceId` (D4). Add the team lookup to `CreateAsync`, `AssignAsync`, `AddCommentAsync`, and `UpsertFromExternalAsync`; `ChangeStatusAsync` already loads it. | **modify** `src/Anvilboard.Application/Issues/IssueService.cs` |
| 2.4 Wrap publication in the same try/catch-and-log shape as `InvokeHookSafelyAsync` so a publisher fault is logged, never thrown. | **modify** `src/Anvilboard.Application/Issues/IssueService.cs` |
| 2.5 Flow `CorrelationId` into every change. | **modify** `src/Anvilboard.Application/Issues/IssueService.cs`, `src/Anvilboard.Api/Endpoints/IssueEndpoints.cs` |

**Correlation note.** `CorrelationContext.FromHeaderOrNew` exists in
`src/Anvilboard.Application/Automation/CorrelationContext.cs` but is currently referenced only by
its own unit test — no endpoint resolves it yet. This phase must therefore also register a scoped
`CorrelationContext` (resolved from `X-Correlation-Id` in the API host, generated per invocation in
the agent host) and inject it into `IssueService`. Treat that as task 2.5a; it is small, but it is
net-new plumbing rather than a reuse.

**Placement.** Publish *after* `await db.SaveChangesAsync(ct)` and outside any ambient transaction,
in the same block that already dispatches `IIssueHook`s. This is what makes AC-RT-001's
"rolled-back mutation produces none" hold structurally rather than by convention: a throw before
`SaveChangesAsync` returns never reaches the publish call.

**Verification:** unit tests in `src/Anvilboard.Application.Tests/Realtime/` with a recording fake
publisher — one change per committed mutation, zero changes when the mutation throws, correct
`WorkspaceId`/`Version`/`CorrelationId`.

### Phase 3 — Bounded coalescing dispatcher

**Goal:** publication is a bounded handoff; bursts collapse; a full queue drops or coalesces
instead of growing.

| Task | Files |
|---|---|
| 3.1 Add `RealtimeChangeCoalescer` — a bounded `Channel<RealtimeChange>` with `BoundedChannelFullMode.DropOldest`, plus a latest-wins map keyed by `(WorkspaceId, IssueId)` for issue changes and by `WorkspaceId` for dashboard changes. | **create** `src/Anvilboard.Application/Realtime/RealtimeChangeCoalescer.cs` |
| 3.2 Add `RealtimeDispatcherHostedService` draining the channel on a short debounce window (default 100 ms, configurable) and forwarding each surviving change to the transport sink. | **create** `src/Anvilboard.Application/Realtime/RealtimeDispatcherHostedService.cs` |
| 3.3 Add `RealtimeOptions` (`DebounceWindow`, `QueueCapacity`) bound from configuration section `Realtime`. | **create** `src/Anvilboard.Application/Realtime/RealtimeOptions.cs` |
| 3.4 Add `AddAnvilboardRealtime(IConfiguration)` registering the coalescer, hosted service, and options — separate from `AddAnvilboardApplication()` per D2. | **modify** `src/Anvilboard.Application/ServiceCollectionExtensions.cs` |

`PublishAsync` becomes `TryWrite` onto the bounded channel: synchronous, allocation-light, and
incapable of awaiting a client. That is the mechanical guarantee behind AC-RT-003's "a slow client
does not delay the mutation response".

**Verification:** `src/Anvilboard.Application.Tests/Realtime/RealtimeCoalescingTests.cs` — N rapid
changes to one issue yield one delivered change carrying the highest version; an over-capacity
burst records drops without blocking the writer.

### Phase 4 — SignalR hub and transport

**Goal:** authorized browsers receive workspace-scoped envelopes; unauthorized ones receive
nothing and cannot infer another workspace exists.

| Task | Files |
|---|---|
| 4.1 Add `WorkspaceRealtimeHub` with `OnConnectedAsync` authenticating via `IWorkspaceAuthorizationService`, checking `Permission.ReadBoard`, and joining exactly one group named from `ActorContext.WorkspaceId`. Expose **no** client-callable join method. | **create** `src/Anvilboard.Infrastructure/Realtime/WorkspaceRealtimeHub.cs` |
| 4.2 Add `SignalRRealtimeUpdatePublisher` implementing the transport sink the dispatcher forwards to, sending to `Clients.Group(workspaceGroup)`. | **create** `src/Anvilboard.Infrastructure/Realtime/SignalRRealtimeUpdatePublisher.cs` |
| 4.3 Read the credential from the SignalR access-token query parameter *or* the `anvilboard_session` cookie, reusing `WorkspaceAuthorizationMiddleware.SessionCookieName` and `ChannelCredential.FromApiToken`. Abort the connection on failure. | **modify** `src/Anvilboard.Infrastructure/Realtime/WorkspaceRealtimeHub.cs` |
| 4.4 Add `builder.Services.AddSignalR()`, `AddAnvilboardRealtime(builder.Configuration)`, the publisher override, and `app.MapHub<WorkspaceRealtimeHub>("/hubs/workspace")` after `UseMiddleware<WorkspaceAuthorizationMiddleware>()`. | **modify** `src/Anvilboard.Api/Program.cs` |
| 4.5 Add the `Microsoft.AspNetCore.SignalR.Core` reference if `Anvilboard.Infrastructure` (a plain `Microsoft.NET.Sdk` project) cannot see the hub types; otherwise use `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. | **modify** `src/Anvilboard.Infrastructure/Anvilboard.Infrastructure.csproj` |

**Task 4.5 is the one real friction point.** `Anvilboard.Infrastructure` targets
`Microsoft.NET.Sdk`, not `Microsoft.NET.Sdk.Web`, so `Hub<T>` and `IHubContext<T>` are not
available by default. Adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` is the
lighter option and keeps the project non-web-SDK; if that proves awkward, the alternative is to
move both files into `src/Anvilboard.Api/Realtime/` and keep only the transport-neutral sink
interface in Infrastructure. Decide during implementation and record the outcome in
[`realtime-updates.md`](./realtime-updates.md)'s File Structure section.

**Verification:** `src/Anvilboard.Api.Tests/Realtime/WorkspaceHubAuthorizationTests.cs` — an
unauthenticated connect is rejected with `WORKSPACE_ACCESS_DENIED`; a workspace-A client receives
no workspace-B event; there is no hub method a client can call to join an arbitrary group.

### Phase 5 — Angular client

**Goal:** a visible board converges in place, preserving selection and scroll, with at most one
re-render per debounce window.

| Task | Files |
|---|---|
| 5.1 Add the `@microsoft/signalr` dependency. | **modify** `src/anvilboard-web/package.json` |
| 5.2 Add `RealtimeBoardSyncService` — builds a `HubConnection` to `/hubs/workspace` with automatic reconnect, and exposes an observable/signal of received changes. | **create** `src/anvilboard-web/src/app/core/realtime-board-sync.service.ts` |
| 5.3 Add realtime envelope types mirroring the C# records. | **modify** `src/anvilboard-web/src/app/core/models.ts` |
| 5.4 Consume the service in `BoardPage`: on `issue.changed`, patch the matching entry in the `issues` signal in place; only fall back to `refresh()` on an unknown event type or a version gap. Preserve `selectedIssue`. | **modify** `src/anvilboard-web/src/app/board/board-page/board-page.ts` |
| 5.5 On reconnect, call `refresh()` once — the documented re-fetch recovery path, never server-side replay. | **modify** `src/anvilboard-web/src/app/core/realtime-board-sync.service.ts` |
| 5.6 Proxy the hub path for `ng serve`. | **modify** `src/anvilboard-web/proxy.conf.json` (add `/hubs` with `"ws": true`) |

Task 5.6 is easy to miss: `proxy.conf.json` currently proxies only `/api` and `/webhooks`, so
without a `/hubs` entry with WebSocket support the dev-server workflow in
[`DEVELOPMENT.md`](../../DEVELOPMENT.md) silently fails to connect.

`BoardPage` today replaces the entire `issues` signal on every `refresh()`. Task 5.4 is what turns
AC-RT-004 from "fewer requests" into "no full redraw": the in-place patch must update the single
matching array element rather than re-assigning the array wholesale.

**Verification:** `src/anvilboard-web/src/app/core/realtime-board-sync.service.spec.ts` — a burst
of same-issue events produces one render pass; an unknown event type triggers exactly one
re-fetch; reconnect triggers exactly one re-fetch.

### Phase 6 — Observability

**Goal:** the metrics the spec's Error Handling table promises actually exist.

| Task | Files |
|---|---|
| 6.1 Add a `Meter` exposing publication latency (commit → publish attempt), coalesced count, dropped count, failed-send count, and active connection count. | **create** `src/Anvilboard.Application/Realtime/RealtimeMetrics.cs` |
| 6.2 Instrument the coalescer, dispatcher, and hub with those counters. | **modify** Phase 3 and Phase 4 files |

The publication-latency histogram is what makes `NFR-PERF-002`'s 500 ms p95 measurable rather than
aspirational; without it AC-RT-003 has no evidence source.

**Verification:** `src/Anvilboard.Api.Tests/Realtime/RealtimeSlowClientIsolationTests.cs` — a
deliberately slow client leaves mutation-response latency unchanged and increments the
coalesced/dropped counters rather than growing a queue.

### Phase 7 — Plugin event relay *(optional; FR-INT-006 / AC-RT-006)*

**Goal:** an approved `github.pull_request.merged` event reaches the workspace without touching
lifecycle-hook dispatch.

| Task | Files |
|---|---|
| 7.1 Add `IPluginEventPublisher` with a typed event-identifier + payload contract. | **create** `src/Anvilboard.Plugins.Abstractions/IPluginEventPublisher.cs` |
| 7.2 Add a relay that maps eligible plugin events onto `RealtimeChange` and forwards them through the same coalescer. | **create** `src/Anvilboard.Application/Realtime/PluginEventRelay.cs` |
| 7.3 Publish `github.pull_request.merged` from the GitHub integration. | **modify** `src/Anvilboard.Integrations.GitHub/` |

This phase crosses into [`integration-and-plugin-platform.md`](./integration-and-plugin-platform.md)
(`AC-IPP-113`). Per D6 it may ship as a follow-on; Phases 1–6 satisfy `FR-WRK-014` and
`NFR-PERF-002` without it. If it is deferred, `AC-RT-006` stays open and
[`realtime-updates.md`](./realtime-updates.md)'s Status row must say so.

## 5. Acceptance-criteria traceability

| AC | Delivered by | Verified by |
|---|---|---|
| `AC-RT-001` — committed-only versioned publication | Phase 2 (publish strictly after `SaveChangesAsync`) | `RealtimeIssuePublicationTests` |
| `AC-RT-002` — cross-workspace isolation | Phase 4 (group derived from `ActorContext.WorkspaceId`; no client-callable join) | `WorkspaceHubAuthorizationTests` |
| `AC-RT-003` — 500 ms p95 attempt; no mutation delay | Phase 3 (bounded non-awaiting handoff) + Phase 6 (latency histogram) | `RealtimeSlowClientIsolationTests` |
| `AC-RT-004` — burst coalescing without full redraw | Phase 3 (latest-wins coalescing) + Phase 5.4 (in-place patch) | `RealtimeCoalescingTests`, `realtime-board-sync.service.spec.ts` |
| `AC-RT-005` — reconnect by re-fetch, no replay | Phase 5.5 (reconnect → single `refresh()`) | `realtime-board-sync.service.spec.ts` |
| `AC-RT-006` — plugin event relay | Phase 7 | `PluginEventRelayTests` (new, with Phase 7) |

## 6. Test cases to add

`docs/anvilboard/test-cases.md` currently has no realtime cases at all — §3.6 contains only
`TC-PERF-001`–`003`, and §5.1 lists no row for `FR-WRK-014` or `NFR-PERF-002`. These
`TC-RT-*` cases close that gap and are added by this change as a new §3.7, with matching rows in
§1.2, §5.1, §5.3, §5.4, §6, and §7:

| TC ID | Title | Priority |
|---|---|---:|
| `TC-RT-001` | A committed issue mutation publishes exactly one workspace-scoped versioned change; a failed mutation publishes none | P1 |
| `TC-RT-002` | A client authorized only for workspace A receives no workspace-B event and cannot join an arbitrary group | P1 |
| `TC-RT-003` | A deliberately slow/disconnected client does not delay the mutation response or grow an unbounded queue | P1 |
| `TC-RT-004` | A burst of updates to one issue yields coalesced notifications and no full board redraw | P1 |
| `TC-RT-005` | A reconnecting client converges through a single documented re-fetch without server-side replay | P1 |

`AC-RT-006` is intentionally uncovered while Phase 7 is deferred (D6); it is tracked as an open
gap rather than silently assumed.

## 7. Risks and open items

| Risk | Impact | Mitigation |
|---|---|---|
| `Anvilboard.Infrastructure` is not a web SDK project | Hub types unavailable; Phase 4 stalls | Task 4.5 — add `FrameworkReference Microsoft.AspNetCore.App`, or relocate the hub to `Anvilboard.Api`. Decide early; it changes two file paths. |
| `CorrelationContext` is currently unused plumbing | `RealtimeChange.CorrelationId` has no real source | Task 2.5a registers and populates it; without this the field is decorative. |
| `Issue.Version` is `int`, spec says `long` | Wire/domain mismatch | D3 — widen at the publisher boundary; no migration. |
| `Anvilboard.Agent` shares the application DI | CLI host would need a hub it cannot host | D2 — null publisher by default, SignalR registered only by the API host. |
| `IPluginEventPublisher` does not exist | `AC-RT-006` unreachable | D6 — Phase 7 is separable; `AC-RT-006` stays explicitly open until then. |
| Hub bypasses `WorkspaceAuthorizationMiddleware` | Silent authorization hole | D5 — hub calls `IWorkspaceAuthorizationService` directly and `WorkspaceHubAuthorizationTests` asserts the denial path. |
| `proxy.conf.json` lacks a `/hubs` entry | Dev-server workflow appears broken | Task 5.6. |

## 8. Sequencing summary

```text
Phase 1  contracts + null publisher        (no behavior change)
Phase 2  publish from IssueService         → AC-RT-001
Phase 3  bounded coalescing dispatcher     → AC-RT-003, AC-RT-004 (server half)
Phase 4  SignalR hub + transport           → AC-RT-002
Phase 5  Angular client                    → AC-RT-004 (client half), AC-RT-005
Phase 6  metrics                           → AC-RT-003 evidence
Phase 7  plugin event relay (optional)     → AC-RT-006
```

Phases 1–6 close `CRIT-002` for `FR-WRK-014` and `NFR-PERF-002` and move
[`overview.md`](./overview.md)'s row 7 from *Not Started* to *Partial*. Only Phase 7 completing
moves it to full coverage of the component's SRS refs.
