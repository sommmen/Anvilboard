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
  surface built on [`dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface) that calls the exact same
  application services the web UI calls, so a coding agent lists, creates, and updates issues
  through the identical code path a person uses.

## Repository layout

```text
Anvilboard/
├── README.md              this file
├── SPEC.md                (historical) as-built PoC architecture — superseded, see docs/anvilboard/
├── FUNCTIONAL_SPEC.md      (historical) original product scope — superseded, see docs/anvilboard/
├── PLUGINS.md              (historical) original plugin guide — superseded, see docs/features/
├── DEVELOPMENT.md          local dev workflow
├── CONTRIBUTING.md         how to propose changes
├── CHANGELOG.md            notable changes per version
├── LICENSE                 MIT
├── ideas/anvilboard/       product vision (draft.md)
├── docs/
│   ├── project-anvilboard.md          feature decomposition / manifest
│   ├── anvilboard/                    prd.md, srs.md, tech-design.md, test-cases.md
│   └── features/                      per-component implementation-facing specs
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

See [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) for the canonical
architecture, domain model, REST surface, and data flow, and
[`docs/features/`](docs/features/) for per-component detail. [`SPEC.md`](SPEC.md) is the retained
historical snapshot of the original proof-of-concept build.

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
calls — see [`docs/features/agent-and-automation-surface.md`](docs/features/agent-and-automation-surface.md)
for the canonical CLI/MCP/REST contract, idempotency, and error-handling rules.

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
[`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md)
(canonical) or [PLUGINS.md](PLUGINS.md) (historical PoC snapshot).

## Documentation

Anvilboard follows the Spec-Forge documentation chain: vision → PRD → SRS → technical design →
feature specs → test cases, with stable requirement IDs threaded through every layer.

| Doc | Answers |
|---|---|
| [`ideas/anvilboard/draft.md`](ideas/anvilboard/draft.md) | What's the product vision and why does it need to exist |
| [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md) | Who is this for, goals, priorities, success measures |
| [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) | Formal functional/non-functional requirements (`FR-*`/`NFR-*`), acceptance criteria, traceability |
| [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) | How is it built: architecture, domain model, REST API, security, operations |
| [`docs/features/`](docs/features/) (start at [`overview.md`](docs/features/overview.md)) | How does a specific component work (authorization, workflow engine, board, integrations, automation surface, audit/recovery) |
| [`docs/anvilboard/test-cases.md`](docs/anvilboard/test-cases.md) | How is this verified |
| [`docs/project-anvilboard.md`](docs/project-anvilboard.md) | How the product is decomposed into delivery streams |
| [DEVELOPMENT.md](DEVELOPMENT.md) | How do I build/run/test this locally |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How do I propose a change |
| [CHANGELOG.md](CHANGELOG.md) | What changed, release by release |
| [SPEC.md](SPEC.md), [FUNCTIONAL_SPEC.md](FUNCTIONAL_SPEC.md), [PLUGINS.md](PLUGINS.md) | (Historical) as-built snapshot of the original proof of concept — superseded by the docs above |

## License

[MIT](LICENSE)
