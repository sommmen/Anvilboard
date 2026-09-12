# Changelog

All notable changes to Anvilboard are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
will adhere to [Semantic Versioning](https://semver.org/) once it has its first tagged release.

## [Unreleased]

### Security

- **Workspace-bound REST/application queries** (closing audit finding `MAJ-022`), designed in
  [`docs/plans/workspace-query-scoping.md`](docs/plans/workspace-query-scoping.md). Previously REST
  authenticated a workspace but then resolved caller-supplied issue, team, and member IDs by primary
  key alone, and an omitted team filter started from every issue in the database — so a caller could
  read or mutate another workspace's data through 13 routes.
  - `WorkspaceId` is now the first, required parameter of every affected method on `IssueService`,
    `IssueLinkService`, `ArtifactService`, and `DashboardService`, all resolving through one shared
    `WorkspaceScopedQueries.InWorkspace` predicate. Leading and non-optional is deliberate: the
    original leak was written as a skipped trailing optional argument, which no longer compiles.
  - New `RestWorkspaceScope` guard — the REST mirror of the existing `AgentWorkspaceScope` —
    validates every caller-supplied ID at the endpoint boundary before a service runs.
  - `IssueLinkService.CreateLinkAsync` compared the two issues' workspaces *to each other*, so a
    pair drawn entirely from a foreign workspace passed the same-workspace check.
    `ArtifactService.RemoveArtifactAsync` did not validate the issue at all. Both are fixed.
  - Ingestion keeps an explicit unscoped entry point (`UpsertFromExternalUnscopedAsync`) because
    polling sync has no authenticated workspace; it fails closed when a source key matches zero or
    more than one team.
  - `CrossWorkspaceIsolationEndpointTests` runs two workspaces on one host and pins every
    ID-addressed route, asserting that denied writes also changed nothing — a single-tenant host
    cannot tell an unscoped query from a correctly scoped one, which is why the original leak
    survived a green suite.

### Changed

- **Breaking — REST not-found responses on ID-addressed routes:** routes that take an entity ID now
  answer `403 WORKSPACE_ACCESS_DENIED` for an ID that is *either* foreign *or* nonexistent — most
  visibly `GET /api/issues/{id}` and `POST /api/issues/{id}/artifacts`, which previously returned
  `404`. Answering differently for the two cases would turn each route into an existence oracle for
  other workspaces' data. `404 REFERENCED_ENTITY_NOT_FOUND` now signals only a dependent lookup that
  fails after workspace scoping has already succeeded (a missing workflow state on a status change,
  or a link ID not attached to an already-scoped issue).
- **Breaking — agent CLI/MCP contract:** all operations now require an automation credential through
  `ANVILBOARD_AGENT__APITOKEN`, enforce credential permissions and workspace ownership, and return
  `{ apiVersion, correlationId, data }` envelopes. The six issue/link mutations now require an
  `idempotencyKey`; backup operations derive the workspace from the credential instead of accepting
  a caller-supplied workspace ID. Each CLI invocation and MCP tool call gets an isolated DI scope,
  and MCP reserves stdout for JSON-RPC while routing diagnostics to stderr.
- REST responses now echo or generate `X-Correlation-Id`, including authentication and
  authorization failures.

### Added

- Artifact application layer (`FR-ART-001`, `FR-ART-002`, closing audit finding `CRIT-003` and
  `MIN-006`), designed in [`docs/plans/artifact-service.md`](docs/plans/artifact-service.md):
  - `IArtifactService`/`ArtifactService` — attach, list, refresh, and remove file, link,
    deployment, and pull-request artifacts on an issue. This is now the sole writer of the
    `Artifacts` table and the sole caller of `IArtifactStore`, making the spec's "sole caller"
    claim true.
  - Dedup-key upsert (`RefreshArtifactAsync`): a provider re-reporting the same correlated
    artifact refreshes the existing row in place rather than accumulating duplicates, preserving
    the original `CreatedAt` and attributing actor. This is the seam GitHub PR correlation
    consumes.
  - Fail-closed content storage: artifact bytes are written through `IArtifactStore` *before* the
    `Artifact` row is added, so a store outage surfaces as `ARTIFACT_STORE_UNAVAILABLE` (502)
    with no row pointing at content that does not exist.
  - Audit trail: every mutation emits `ArtifactAttached`, `ArtifactRefreshed`, or
    `ArtifactRemoved`, carrying only artifact identity and never artifact content.
  - REST endpoints under `/api/issues/{id}/artifacts` (`GET`, `POST`, and
    `DELETE .../{artifactId}`), guarded by the issue's own mutation permissions. `Refresh` is
    deliberately not exposed — it is reachable only from the owning plugin's correlation logic.
  - `AC-ART-001`–`AC-ART-011` service tests plus API-level tests covering routing, DI, auth, and
    response shape.
- Real-time updates end to end (`AC-RT-001`–`AC-RT-006`, closing audit finding `CRIT-002`):
  - Transport-neutral `IRealtimeUpdatePublisher`/`IRealtimeTransport` seams and versioned
    issue/activity/dashboard/plugin change envelopes (`Anvilboard.Application/Realtime`).
  - Post-commit publication from `IssueService`, fault-isolated so a realtime failure can never fail
    or delay a committed mutation.
  - A bounded, key-coalescing buffer and debounced background dispatcher, so a burst of updates to
    one issue collapses into a single notification and a slow client sheds work instead of growing
    memory without limit.
  - A SignalR `WorkspaceRealtimeHub` at `/hubs/workspace`, authorized by the existing workspace
    middleware and scoped to server-derived `workspace:{id}` groups.
  - An Angular `RealtimeBoardSyncService` plus in-place board reconciliation: only the changed issue
    is re-fetched and swapped, and a reconnect triggers one full re-fetch rather than server replay.
  - Realtime counters and publication-latency metrics.
  - An approval-gated plugin event relay (`IPluginEventPublisher`): a plugin event reaches browsers
    only when an operator lists its type in `Realtime:RelayedPluginEventTypes`. The GitHub plugin
    reports `github.pull_request.merged`.
  - `TC-RT-001`–`TC-RT-006` test cases and coverage matrix rows in
    `docs/anvilboard/test-cases.md`.
- Initial domain model: `Issue`, `Team`, `Member`, `Comment`, `ActivityEvent`, `ExternalLink`,
  `Workspace`, `Project`, `Label`, strongly-typed IDs, and the `IssueStatus`/`IssuePriority`/
  `IntegrationProvider` enums (`Anvilboard.Domain`).
- Plugin contract: `IIngestionSource`, `IWebhookReceiver`, `IIssueHook`, `IPluginRegistry`,
  `NormalizedIssue`/`NormalizedComment`, `PluginManifest` (`Anvilboard.Plugins.Abstractions`).
- EF Core + SQLite persistence with automatic migration on startup, and a reflection-based plugin
  loader for out-of-repo plugin assemblies (`Anvilboard.Infrastructure`).
- Shared application layer — `IssueService`, `DashboardService`, and the `SyncCoordinator`
  polling loop — consumed identically by the REST API and the agent surface
  (`Anvilboard.Application`).
- First-class GitHub ingestion + webhook plugin (`Anvilboard.Integrations.GitHub`).
- First-class Linear-style-tracker ingestion + webhook plugin (`Anvilboard.Integrations.Linear`).
- ASP.NET Core minimal-API host serving the REST API, the webhook dispatch route, and the built
  Angular SPA from a single process (`Anvilboard.Api`).
- CLI + MCP dual-mode agent surface built on `dotnet-agent-surface`, exposing all board
  operations (`list-issues`, `get-issue`, `create-issue`, `change-issue-status`, `assign-issue`,
  `comment-on-issue`, `dashboard-summary`) to coding agents (`Anvilboard.Agent`).
- Angular 22 standalone-component web client: Kanban board (quick-create, drag-free status
  changes, issue detail panel with comments) and a dashboard (7-day created/completed counts,
  status/source breakdowns, open load by assignee) (`anvilboard-web`).
- Project documentation: [README.md](README.md), [FUNCTIONAL_SPEC.md](FUNCTIONAL_SPEC.md),
  [SPEC.md](SPEC.md), [PLUGINS.md](PLUGINS.md), [DEVELOPMENT.md](DEVELOPMENT.md),
  [CONTRIBUTING.md](CONTRIBUTING.md), and this changelog.

### Fixed

- `IArtifactStore` is now registered in dependency injection. `SqliteArtifactStore` existed and was
  tested but was never wired up, so any consumer resolving `IArtifactStore` at runtime would have
  failed.
- `IssueService.ChangeStatusAsync` now delegates transition legality to
  `IWorkflowService.ValidateTransitionAsync` instead of mutating the legacy `IssueStatus` enum
  unconditionally: it resolves the requested status to the workspace's seeded `WorkflowState`,
  validates the transition, and only on approval updates `Status`, `WorkflowStateId`, and
  increments `Version`, throwing `WorkflowTransitionDeniedException` (mapped to HTTP 409 by
  `IssueEndpoints`) on denial. Closes the integration gap between the Issue & Board Service and the
  Workflow Engine foundation (`Anvilboard.Application`, `Anvilboard.Api`).
- `anvilboard-web/proxy.conf.json` pointed `ng serve`'s dev proxy at port `5289`, which doesn't
  match `Anvilboard.Api`'s actual `launchSettings.json` port (`5089`); corrected so the Angular
  dev server workflow described in [DEVELOPMENT.md](DEVELOPMENT.md) works out of the box.
- `POST /api/auth/bootstrap` now seeds the same six default `WorkflowState` rows and linear
  `WorkflowTransition` adjacency that migration `20260908093300_AddWorkflowStates.cs` seeds for
  pre-existing workspaces (closing audit finding `MAJ-021`). Previously, bootstrap created only the
  workspace and administrator, so `POST /api/issues` against a freshly bootstrapped (self-hosted)
  workspace failed with an opaque 500 because no workflow state existed to assign to the new issue.
