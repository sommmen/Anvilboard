# Changelog

All notable changes to Anvilboard are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
will adhere to [Semantic Versioning](https://semver.org/) once it has its first tagged release.

## [Unreleased]

### Added

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

- `anvilboard-web/proxy.conf.json` pointed `ng serve`'s dev proxy at port `5289`, which doesn't
  match `Anvilboard.Api`'s actual `launchSettings.json` port (`5089`); corrected so the Angular
  dev server workflow described in [DEVELOPMENT.md](DEVELOPMENT.md) works out of the box.
