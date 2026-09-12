# Changelog

All notable changes to Anvilboard are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
will adhere to [Semantic Versioning](https://semver.org/) once it has its first tagged release.

## [Unreleased]

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
