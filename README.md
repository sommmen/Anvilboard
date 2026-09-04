# Anvilboard

**A local-first issue tracker and dashboard you host in-process, next to whatever else you're
already running.**

Anvilboard pulls work in from wherever it already lives — GitHub issues, a Linear-style tracker,
Slack, or a coding agent pushing a task — and lands it on one task board and dashboard that run as
a single small process with a single SQLite file. No SaaS account, no Docker Compose stack, no
separate database server.

```
                 ┌──────────────┐   pulls/pushes issues in    ┌───────────────────────────┐
 GitHub issues ─▶│              │◀────────────────────────────│                           │
 Linear-style  ─▶│  Anvilboard  │                              │  You / your team          │
 tracker         │  (one        │   REST API + web UI          │  agents (CLI, MCP, hooks) │
 Slack (plugin)─▶│  process,    │◀────────────────────────────▶│                           │
 Agent push    ─▶│  one .db)    │                              └───────────────────────────┘
                 └──────────────┘
```

## Why

Most issue trackers assume you host it as the system of record and everyone types into it
directly. Anvilboard assumes the opposite: your tickets already exist somewhere (GitHub, a
tracker, a chat message, an agent's output) and you want one place — running on your machine or a
small box you control — that pulls all of that into a single board and dashboard, without giving
up ownership of the data or standing up infrastructure to get it.

## Features

- **Single-process host.** `Anvilboard.Api` serves the REST API *and* the built Angular SPA from
  one executable. `dotnet run` (or one published binary) is the whole deployment.
- **Local-first storage.** One SQLite file. Back it up by copying a file; move it by copying a
  file.
- **Kanban board + dashboard.** Backlog → Todo → In Progress → In Review → Done/Cancelled columns,
  quick-create, an issue detail panel, and a dashboard with throughput, status/source breakdowns,
  and open-load-by-assignee.
- **GitHub and Linear-style integrations as first-class plugins.** Both ship in-repo, both are
  ordinary implementations of the same three plugin interfaces every third-party plugin uses —
  there is no special-cased "built-in" API.
- **A small, stable plugin surface.** Three interfaces —
  [`IIngestionSource`](src/Anvilboard.Plugins.Abstractions/IIngestionSource.cs) (poll),
  [`IWebhookReceiver`](src/Anvilboard.Plugins.Abstractions/IWebhookReceiver.cs) (push), and
  [`IIssueHook`](src/Anvilboard.Plugins.Abstractions/IIssueHook.cs) (react) — are enough to add a
  new source (a private Slack ticket-creation plugin, for example) without touching core code. See
  [`PLUGINS.md`](PLUGINS.md).
- **Agents are first-class users of the board, not bolted on.** `Anvilboard.Agent` is a CLI+MCP
  surface built on [`dotnet-agent-surface`](../dotnet-agent-surface) that calls the exact same
  application services the web UI calls, so a coding agent lists, creates, and updates issues
  through the identical code path a person uses.

## Repository layout

```text
Anvilboard/
├── README.md              this file
├── SPEC.md                technical architecture (as-built)
├── FUNCTIONAL_SPEC.md      what the product does and for whom
├── PLUGINS.md              how to write a plugin
├── DEVELOPMENT.md          local dev workflow
├── CONTRIBUTING.md         how to propose changes
├── CHANGELOG.md            notable changes per version
├── LICENSE                 MIT
└── src/
    ├── Anvilboard.Domain                    Entities, enums, no dependencies on anything else
    ├── Anvilboard.Plugins.Abstractions       The three plugin interfaces + PluginManifest
    ├── Anvilboard.Infrastructure             EF Core/SQLite persistence, plugin registry
    ├── Anvilboard.Application                IssueService, DashboardService, SyncCoordinator
    ├── Anvilboard.Integrations.GitHub        First-class GitHub plugin (poll + webhook)
    ├── Anvilboard.Integrations.Linear        First-class Linear-style-tracker plugin
    ├── Anvilboard.Api                        ASP.NET Core host: REST API + serves the built SPA
    ├── Anvilboard.Agent                      CLI + MCP agent surface (dotnet-agent-surface)
    └── anvilboard-web/                       Angular client (board + dashboard)
```

See [SPEC.md](SPEC.md) for the full architecture, domain model, REST surface, and data flow.

## Quickstart

**Prerequisites:** .NET SDK 10, Node.js 20+ (Angular 22 requires it), npm.

> `Anvilboard.Agent` references `dotnet-agent-surface` by project reference (it isn't published to
> NuGet yet), so clone it as a sibling of this repo: `../dotnet-agent-surface` relative to
> `Anvilboard/`. `Anvilboard.Api` and the Angular client have no such dependency and build
> standalone.

```powershell
# 1. Build the Angular client — its production build lands directly in Anvilboard.Api/wwwroot
cd src/anvilboard-web
npm install
npm run build

# 2. Run the API host (serves the API and the SPA you just built)
cd ../Anvilboard.Api
dotnet run
```

Open `http://localhost:5089` — you'll see the board. A fresh SQLite file (`anvilboard.db`) and
schema are created automatically on first run; there is no separate migration step.

For iterating on the Angular client with hot reload against a running Api, see the frontend dev
workflow in [DEVELOPMENT.md](DEVELOPMENT.md).

### Using the agent surface

```powershell
cd src/Anvilboard.Agent
dotnet run -- issues create-issue --teamId <guid> --title "Fix the thing"
dotnet run -- issues list-issues --status InProgress
dotnet run -- mcp   # long-running MCP server over stdio, for an MCP-aware agent host
```

The CLI and MCP surface both call `BoardAgentService`, the same application services the REST API
calls — see [SPEC.md § Agent surface](SPEC.md#agent-surface-climcp).

## Configuring integrations

GitHub and Linear-style polling/webhooks are opt-in and off by default. Configure them under
`Plugins:<key>` in `appsettings.json` (or `ANVILBOARD_Plugins__<key>__...` environment variables
for the agent host):

```json
{
  "Plugins": {
    "github": { "Enabled": true, "Token": "...", "Repositories": ["owner/repo"], "TeamKey": "ENG" },
    "linear": { "Enabled": true, "ApiKey": "...", "TeamKeys": ["ENG"], "TeamKey": "ENG" }
  }
}
```

Private, out-of-repo plugins (e.g. a Slack ticket-creation plugin) are enabled by dropping a
compiled DLL path into `Plugins:AssemblyPaths` — no source reference from Anvilboard needed. See
[PLUGINS.md](PLUGINS.md).

## Documentation

| Doc | Answers |
|---|---|
| [FUNCTIONAL_SPEC.md](FUNCTIONAL_SPEC.md) | Who is this for, what can they do, what does it *not* do |
| [SPEC.md](SPEC.md) | How is it built: hosting model, domain model, REST API, agent surface, data flow |
| [PLUGINS.md](PLUGINS.md) | How do I add a new ingestion source / webhook / hook |
| [DEVELOPMENT.md](DEVELOPMENT.md) | How do I build/run/test this locally |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How do I propose a change |
| [CHANGELOG.md](CHANGELOG.md) | What changed, release by release |

## License

[MIT](LICENSE)
