# Developing Anvilboard

This document is for anyone building, running, or modifying Anvilboard itself. For what the
product does, see [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md) and
[`docs/anvilboard/srs.md`](docs/anvilboard/srs.md); for how it's built, see
[`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md); for writing a plugin, see
[`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md).
The historical PoC-era equivalents ([FUNCTIONAL_SPEC.md](FUNCTIONAL_SPEC.md),
[SPEC.md](SPEC.md), [PLUGINS.md](PLUGINS.md)) are retained for reference but no longer updated.

## Prerequisites

- **.NET SDK 10** (`dotnet --version` should report `10.x`).
- **Node.js 20+** and **npm** (Angular 22 requires Node 20 or newer; this repo was built/verified
  against Node v24, npm 11).
- **[`dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface)** cloned as a sibling directory:

  ```
  repos/
  ├─ Anvilboard/
  └─ dotnet-agent-surface/
  ```

  `Anvilboard.Agent` references it via `ProjectReference` (`../../../dotnet-agent-surface/...`
  relative to `src/Anvilboard.Agent/`) rather than a NuGet package, so the two repos must sit next
  to each other. `Anvilboard.Api` and `anvilboard-web` have no such dependency and can be built
  without `dotnet-agent-surface` present — you only need it if you're touching `Anvilboard.Agent`.

## Repository layout

| Path | What's there |
|---|---|
| `src/Anvilboard.Domain` | Entities, enums, strongly-typed IDs. No project dependencies. |
| `src/Anvilboard.Plugins.Abstractions` | The plugin contract. Depends only on `Anvilboard.Domain`. |
| `src/Anvilboard.Infrastructure` | EF Core `DbContext`, SQLite provider, migrations, plugin registry/loader. |
| `src/Anvilboard.Application` | `IssueService`, `DashboardService`, `SyncCoordinator` — the shared logic layer both the API and the agent surface call. |
| `src/Anvilboard.Integrations.GitHub` | First-class GitHub ingestion + webhook plugin. |
| `src/Anvilboard.Integrations.Linear` | First-class Linear-style-tracker ingestion + webhook plugin. |
| `src/Anvilboard.Api` | ASP.NET Core minimal-API host; serves the REST API, webhook route, and the built SPA. |
| `src/Anvilboard.Agent` | CLI + MCP dual-mode host, built on `dotnet-agent-surface`. |
| `src/anvilboard-web` | Angular 22 standalone-component SPA. |
| `Anvilboard.slnx` | Solution file referencing all eight in-repo projects. |

## Building the backend

From the repo root:

```powershell
dotnet build Anvilboard.slnx
```

This builds all eight projects, including `Anvilboard.Agent` (so `dotnet-agent-surface` must be
present as described above). To build everything except the agent surface (e.g. if you don't have
`dotnet-agent-surface` checked out), build the individual `.csproj` you need instead of the
`.slnx`, e.g. `dotnet build src/Anvilboard.Api/Anvilboard.Api.csproj`.

## Building the frontend

```powershell
cd src/anvilboard-web
npm install
npm run build
```

`angular.json`'s production `outputPath` writes straight into `src/Anvilboard.Api/wwwroot` (no
nested `browser/` subfolder), so `Anvilboard.Api`'s static-file hosting picks up the build with no
extra copy step. Run this before `dotnet run` on `Anvilboard.Api` if you want the API host to also
serve the current UI.

## Running things day-to-day

**Backend + prebuilt UI, single process** (what you'd run to just use the board):

```powershell
cd src/anvilboard-web && npm run build
cd ../Anvilboard.Api
dotnet run
```

Serves the API and the SPA together at `http://localhost:5089` (or
`https://localhost:7205`/`http://localhost:5089` with the `https` launch profile).

**Backend + Angular dev server, for frontend work** (hot reload):

```powershell
# terminal 1
cd src/Anvilboard.Api
dotnet run

# terminal 2
cd src/anvilboard-web
npm start   # ng serve
```

`ng serve` proxies `/api` and `/webhooks` to `http://localhost:5089` via `proxy.conf.json`, so the
API host runs standalone and the Angular dev server handles the UI with live reload. Open the URL
`ng serve` prints (typically `http://localhost:4200`), not the API's port, while doing frontend
work this way.

**Agent CLI**, one-shot:

```powershell
cd src/Anvilboard.Agent
dotnet run -- issues list-issues --full
dotnet run -- issues create-issue --teamId <guid> --title "Fix the thing"
```

**Agent MCP server**, long-running (also the only mode that runs the ingestion polling loop):

```powershell
cd src/Anvilboard.Agent
dotnet run -- mcp
```

## Configuration

Both `Anvilboard.Api` and `Anvilboard.Agent` read from `appsettings.json` +
`appsettings.{Environment}.json` + environment variables (prefix `ANVILBOARD_`, double-underscore
for nesting — e.g. `ANVILBOARD_Plugins__github__Token`). See [README.md](README.md#configuring-integrations)
for the `Plugins:github` / `Plugins:linear` shape, and
[`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md)
for `Plugins:AssemblyPaths` (loading out-of-repo plugin DLLs).

The SQLite file path is `Database:DatabasePath`, defaulting to `anvilboard.db` next to the running
executable. Schema is created/updated automatically on startup (`Database.MigrateAsync()`) — there
is no separate migration command to run by hand.

## Testing

There is no automated test project in the solution yet (`Anvilboard.slnx` currently has eight
non-test projects only). Verification so far has been manual/smoke-test based: exercising the REST
API, the Angular UI, and every agent CLI/MCP operation end-to-end against a real SQLite database.
The canonical test strategy and coverage plan going forward is
[`docs/anvilboard/test-cases.md`](docs/anvilboard/test-cases.md).

If you're adding a non-trivial feature, adding a proper test project (`Anvilboard.Application.Tests`
against `IssueService`/`DashboardService` is the highest-value starting point, since both front
ends depend on that layer) is a welcome contribution — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Coding conventions

- **Nullable reference types are enabled everywhere** (`<Nullable>enable</Nullable>`); don't
  suppress warnings with `!` unless the alternative is genuinely worse.
- **Minimal APIs, not controllers**, for `Anvilboard.Api` — one `Endpoints` static class per
  resource group (e.g. `IssueEndpoints`), matching the existing files.
- **Strongly-typed IDs** for domain entities (see `Anvilboard.Domain`) rather than raw `Guid`
  parameters — keeps `CreateIssue(TeamId, ...)` from being callable with a `MemberId` by accident.
- **Application-layer-first**: new board behavior goes into `Anvilboard.Application` and gets
  exposed from there to both `Anvilboard.Api` and `Anvilboard.Agent`'s `BoardAgentService`, never
  implemented directly inside an endpoint or an agent operation.
- **Angular**: standalone components, signals for local state, one flat `BoardApiService` as the
  single `HttpClient` wrapper (mirrors the agent surface's operation set) rather than one service
  per component.
- **No new heavy dependencies** without a good reason — the project's whole premise is staying
  small and low-resource; think twice before adding a message broker, a second database, or a
  container-orchestration requirement.
