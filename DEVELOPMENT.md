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
| `Anvilboard.slnx` | Solution file referencing the in-repo application and test projects. |

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

**Agent CLI**, one-shot. First create an automation credential with
`POST /api/auth/credentials` as a workspace administrator; copy the returned token because only its
hash is stored. Configure that token for every CLI or MCP process:

```powershell
$env:ANVILBOARD_AGENT__APITOKEN = '<automation-credential-token>'
cd src/Anvilboard.Agent
dotnet run -- issues list-issues --full
dotnet run -- issues create-issue --teamId <guid> --title "Fix the thing" --idempotencyKey "dev-create-001"
```

Every operation is authenticated and authorized within the credential's workspace. Each invocation
gets an isolated dependency-injection scope and correlation ID, and returns
`{"apiVersion":"1","correlationId":"...","data":...}`. The six workspace-data mutations require
`--idempotencyKey value`; the key is scoped by workspace, actor, and operation and is retained for
30 days. Use `--name value` syntax rather than `name=value`.

The surface exposes 13 operations, all of which are also registered as MCP tools:

| Category | Operation | Idempotency key required |
|---|---|---|
| `issues` | `list-issues`, `get-issue` | No (reads) |
| `issues` | `create-issue`, `change-issue-status`, `assign-issue`, `comment-on-issue` | **Yes** |
| `issues` | `list-issue-links` | No (read) |
| `issues` | `create-issue-link`, `remove-issue-link` | **Yes** |
| `dashboard` | `dashboard-summary` | No (read) |
| `backup` | `create-backup`, `list-backups`, `verify-backup` | No |

Restore is deliberately **not** exposed to the agent surface — it is an administrator-only REST
operation (`DR-AGT-004`). Effective permissions are the intersection of the credential's grants and
the actor's workspace role.

**Agent MCP server**, long-running (also the only mode that runs the ingestion polling loop):

```powershell
$env:ANVILBOARD_AGENT__APITOKEN = '<automation-credential-token>'
cd src/Anvilboard.Agent
dotnet run -- mcp
```

MCP creates and disposes a scope and correlation ID per tool call. Standard output is reserved for
JSON-RPC; diagnostics are written to standard error. This authenticated, versioned, idempotent
contract is intentionally breaking relative to the earlier agent surface.

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

Backup artifacts are written to `Database:BackupDirectory`, defaulting to a `backups/` folder
alongside the database file.

## Backup and recovery drill

`NFR-AVL-001` requires a verified backup/restore drill at least once per release. The automated
round-trip test (`src/Anvilboard.Application.Tests/Backup/BackupRoundTripTests.cs`) proves the
mechanism works; this manual drill proves it works against a real deployment, with real data
volumes and a real file path. Run it against a **copy** of production data, never production
itself.

All routes require the `ManageBackupRestore` permission, which is Administrator-only. A backup is
whole-instance (one SQLite file), so restoring one workspace necessarily restores every workspace
in that file — the restore call fails closed if the live database contains a workspace the artifact
doesn't.

1. **Create a backup.**

   ```bash
   curl -X POST http://localhost:5000/api/backups -H "Authorization: Bearer $TOKEN"
   ```

   The response is a manifest containing `backupId`, `checksumSha256`, `schemaVersion`, `sizeBytes`,
   and the full set of workspaces the artifact covers. Record the `backupId`.

2. **Verify the artifact** without touching the live database:

   ```bash
   curl -X POST http://localhost:5000/api/backups/$BACKUP_ID/verify -H "Authorization: Bearer $TOKEN"
   ```

   A healthy artifact returns `"verified": true`. A `422 BACKUP_INTEGRITY_INVALID` response names
   the specific failed check (checksum, manifest, SQLite integrity, schema compatibility, or
   workspace subset).

3. **Note a known-good data point** you can assert on after the restore — for example an issue
   title, or the issue count of a board.

4. **Mutate something** after the backup was taken, so the restore is observably a restore rather
   than a no-op.

5. **Restore**, confirming with the exact workspace slug. The confirmation is compared
   case-sensitively and is a deliberate speed bump:

   ```bash
   curl -X POST http://localhost:5000/api/backups/$BACKUP_ID/restore \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"confirmedWorkspaceSlug":"your-slug"}'
   ```

   While the restore runs, the API closes admission and drains in-flight work; other `/api`
   requests receive `429` with `Retry-After: 5` until it completes. Every validation check runs
   *before* the live file is touched, so a rejected restore leaves the running instance unchanged.

6. **Confirm recovery**: the mutation from step 4 is gone, the data point from step 3 is back, and
   the `AuditEvents` table in the restored database contains a `workspace.restore.completed` event.
   (The completed event is written to the *restored* database, which is where an auditor will look;
   a *failed* restore is audited in the untouched live database.)

7. **Record the wall-clock time** from step 5 to step 6 and compare it against the recovery-time
   objective in `docs/anvilboard/srs.md` (`NFR-AVL-001`).

If a restore fails *after* the file swap — the only window where the live database has already been
replaced — the pre-swap safety copy written next to the database as
`<database>.pre-restore-<timestamp>` is the recovery path, and admission stays closed so nothing
writes to a half-restored file. These safety copies are not pruned automatically; delete old ones
once a drill is confirmed good.

## Testing

The xUnit projects cover the Workflow Engine, endpoint authorization, the agent surface, backup and
restore, artifacts, real-time delivery, and webhook validation. A full `dotnet test Anvilboard.slnx`
run is **320 passing, 0 failing**:

| Project | Tests | What it covers |
|---|---:|---|
| `src/Anvilboard.Application.Tests` | 192 | `WorkflowEngine` unit tests: transition validation, state creation validation, archive/reassignment behavior; also covers workspace authorization, issue linking, artifacts (`ArtifactService`), audit redaction, backup/restore round-trips and secret scanning, real-time dispatch/coalescing/plugin-event relay, and the automation surface foundations (`IdempotencyService` replay/reuse detection, `CorrelationContext`, `ErrorCatalogTranslator`). No database file — uses SQLite `DataSource=:memory:` per test. |
| `src/Anvilboard.Infrastructure.Tests` | 41 | Migration integration test (seeds a legacy pre-workflow SQLite schema, runs the real EF Core migrations, asserts default workflow states/transitions were seeded and issues backfilled), plus plugin registry/config-state storage, the SQLite backup archiver and archive store, and the data-protection secret store. |
| `src/Anvilboard.Api.Tests` | 30 | API-host integration tests for workspace authorization endpoints, artifact and backup endpoints, the `X-Correlation-Id` middleware, webhook endpoints, and the real-time SignalR hub (connection authorization, workspace isolation, and mutation isolation from a slow client). |
| `src/Anvilboard.Agent.Tests` | 40 | Agent operation-catalog invariants, request guards, SQLite-backed authorization integration tests (credential authentication, permission enforcement, workspace isolation, actor attribution), and MCP stdout isolation. Requires the sibling `dotnet-agent-surface` checkout. |
| `src/Anvilboard.Integrations.GitHub.Tests` | 12 | GitHub webhook signature validation and issue-event mapping. |
| `src/Anvilboard.Integrations.Linear.Tests` | 5 | Linear webhook signature validation and issue-event mapping. |
| `tests/Anvilboard.IntegrationTests` | 0 | Reserved for cross-cutting integration coverage; still an empty scaffold. |

Run focused tests with:

```powershell
dotnet test src/Anvilboard.Application.Tests/Anvilboard.Application.Tests.csproj
dotnet test src/Anvilboard.Infrastructure.Tests/Anvilboard.Infrastructure.Tests.csproj
dotnet test src/Anvilboard.Api.Tests/Anvilboard.Api.Tests.csproj
dotnet test src/Anvilboard.Agent.Tests/Anvilboard.Agent.Tests.csproj
dotnet test src/Anvilboard.Integrations.GitHub.Tests/Anvilboard.Integrations.GitHub.Tests.csproj
dotnet test src/Anvilboard.Integrations.Linear.Tests/Anvilboard.Integrations.Linear.Tests.csproj
dotnet test tests/Anvilboard.IntegrationTests/Anvilboard.IntegrationTests.csproj
```

`dotnet test Anvilboard.slnx` also works once `dotnet-agent-surface` is checked out next to this
repo, since the full solution build includes `Anvilboard.Agent` and its tests. The
`tests/Anvilboard.IntegrationTests` project is still an empty scaffold; real-time coverage lives in
`src/Anvilboard.Application.Tests/Realtime` and `src/Anvilboard.Api.Tests/Realtime` instead.

Frontend tests remain in `src/anvilboard-web` and run through Angular/Vitest (`npm test`). The
feature specification paths `src/Anvilboard.Web` and `tests/Anvilboard.Web.Tests` are stale; do not
create a separate frontend test project at those paths.

Coverage is now broad but not uniform: `IssueService`, `DashboardService`, and `SyncCoordinator`
still have no dedicated test file of their own, and there is no CLI/MCP contract-equivalence test
project. The canonical test strategy and coverage plan going forward is
[`docs/anvilboard/test-cases.md`](docs/anvilboard/test-cases.md). If you're adding a non-trivial
feature elsewhere, adding tests for it (following the pattern in
`Anvilboard.Application.Tests`) is a welcome contribution — see [CONTRIBUTING.md](CONTRIBUTING.md).

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
