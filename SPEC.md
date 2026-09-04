# Anvilboard — technical specification

Anvilboard is a local-first, single-process issue tracker and dashboard. It hosts its own REST
API and its own web UI from one executable, is meant to run comfortably on a laptop or a small
VM next to whatever else you're doing, and pulls work in from remote systems (GitHub, a
Linear-style tracker, Slack, or an agent pushing tasks) instead of requiring you to host it
against someone else's SaaS backend.

This document describes the architecture as built. For plugin authoring, see
[`PLUGINS.md`](PLUGINS.md).

## Goals and non-goals

**Goals**
- Run as a single process, single executable, single SQLite file. No required Docker Compose
  stack, no separate database server, no message broker.
- Client/server architecture even though it's one process: a well-defined REST API, consumed by
  both a browser SPA and a CLI/MCP agent surface, so nothing is UI-only or agent-only.
- Local-first: your data lives in your SQLite file, under your control, not in a SaaS account.
- Pull work *in* from wherever it already lives (GitHub issues, a Linear-style tracker, Slack,
  agent-originated tasks) rather than becoming another system of record you have to duplicate
  work into.
- Cheap to extend: three small interfaces, no plugin SDK to learn beyond them.
- Agents are first-class users of the board, not an afterthought bolted onto the UI.

**Non-goals**
- Multi-tenant SaaS hosting, horizontal scale-out, or a distributed database.
- Real-time collaborative editing (websockets/CRDTs) — out of scope for v1.
- A generic workflow/BPM engine. Issue states are a fixed enum, not user-defined state machines.

## Process and hosting model

```
┌─────────────────────────────── Anvilboard.Api (one process) ───────────────────────────────┐
│                                                                                              │
│  ASP.NET Core minimal API host                                                              │
│   ├─ /api/teams, /api/members, /api/issues, /api/dashboard/summary   (REST, JSON)           │
│   ├─ /webhooks/{provider}                                            (plugin dispatch)      │
│   └─ UseStaticFiles() + MapFallbackToFile("index.html")               (serves the Angular    │
│                                                                         production build      │
│                                                                         from wwwroot/)        │
│                                                                                              │
│  Anvilboard.Application  — IssueService, DashboardService, SyncCoordinator (BackgroundService)│
│  Anvilboard.Infrastructure — EF Core + SQLite, PluginRegistry                                │
│  Anvilboard.Integrations.GitHub / .Linear — first-class IIngestionSource/IWebhookReceiver     │
│  (+ any third-party plugin DLLs listed in Plugins:AssemblyPaths, loaded by reflection)        │
│                                                                                              │
└──────────────────────────────────────────────────────────────────────────────────────────────┘

┌────────────────────── Anvilboard.Agent (separate, optional process) ───────────────────────┐
│                                                                                              │
│  Same Application/Infrastructure/Integrations libraries, wired standalone (no ASP.NET host)  │
│  CLI mode:  anvilboard-agent issues list-issues --full                                       │
│  MCP mode:  anvilboard-agent mcp   (stdio JSON-RPC; tools/list exposes the same 7 operations) │
│                                                                                              │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

`Anvilboard.Api` and `Anvilboard.Agent` are **separate executables that share every domain and
application-layer library**, each pointed at its own `Database:DatabasePath` (typically the same
SQLite file in a real deployment, separate files only for isolated local testing). Neither one
depends on the other; both depend downward on `Anvilboard.Application` /
`Anvilboard.Infrastructure` / `Anvilboard.Domain` / the two integration projects.

In production, running the Api host is normally enough — a human uses the SPA it serves, and an
agent can talk to `Anvilboard.Agent` in MCP mode alongside it, both operating on the same SQLite
file. Running the Agent host **also** runs its own `SyncCoordinator` in MCP mode (long-running),
but not in single-shot CLI mode — see [Agent surface](#agent-surface-climcp) below.

### Why one process, one SQLite file

This is the direct implementation of the "low-resource, no huge Docker file" requirement: EF Core
against SQLite needs no server process, `dotnet publish` produces a single self-contained
executable if desired, and the Angular build output is copied straight into the API host's
`wwwroot/` at build time — so "run Anvilboard" is `dotnet Anvilboard.Api.dll` (or the published
`.exe`), full stop.

## Solution layout

| Project | Responsibility |
|---|---|
| [`Anvilboard.Domain`](src/Anvilboard.Domain) | Entities (`Issue`, `Team`, `Member`, `Comment`, `ActivityEvent`, `ExternalLink`, `Workspace`, `Project`, `Label`), strongly-typed IDs, enums (`IssueStatus`, `IssuePriority`, `IntegrationProvider`), and the shared `StronglyTypedIdJsonConverter`. No dependencies on anything else in the solution. |
| [`Anvilboard.Plugins.Abstractions`](src/Anvilboard.Plugins.Abstractions) | The entire plugin contract: `IIngestionSource`, `IWebhookReceiver`, `IIssueHook`, `IPluginRegistry`, `NormalizedIssue`/`NormalizedComment`, `PluginManifest`. Depends only on `Anvilboard.Domain`. Third-party plugins depend on this project and nothing else. |
| [`Anvilboard.Infrastructure`](src/Anvilboard.Infrastructure) | EF Core `AnvilboardDbContext` + SQLite provider + migrations; `PluginRegistry` (DI-registered plugin discovery + reflection-loaded assembly plugins). |
| [`Anvilboard.Application`](src/Anvilboard.Application) | `IssueService` (CRUD/transitions/comments/external upsert), `DashboardService` (read-only aggregates), `SyncCoordinator` (per-plugin polling loop, a `BackgroundService`). This is the layer both the HTTP API and the Agent CLI/MCP surface call — behavior can never diverge between the two front ends because they share this code, not just a schema. |
| [`Anvilboard.Integrations.GitHub`](src/Anvilboard.Integrations.GitHub) / [`.Linear`](src/Anvilboard.Integrations.Linear) | First-class ingestion/webhook plugins implemented against the same `Anvilboard.Plugins.Abstractions` interfaces any third-party plugin would use. |
| [`Anvilboard.Api`](src/Anvilboard.Api) | ASP.NET Core minimal-API host: REST endpoints, webhook dispatch route, static-file hosting of the built SPA. |
| [`Anvilboard.Agent`](src/Anvilboard.Agent) | CLI + MCP dual-mode host built on `dotnet-agent-surface`, exposing the board to coding agents. |
| `anvilboard-web` (`src/anvilboard-web`) | Angular 22 standalone-component SPA: board (Kanban) and dashboard views. |

## Domain model

```
Workspace 1──* Team 1──* Issue *──1 Member (assignee, nullable)
                          │
                          ├──* Comment
                          ├──* ActivityEvent   (append-only audit/event log; also what IIssueHook fires on)
                          ├──* ExternalLink    (Provider + SourceKey → dedupe key for synced issues)
                          └──* LabelId (many-to-many via LabelIds list)
```

- `Issue.Key` (e.g. `ENG-142`) is generated from the owning `Team.Key` + a per-team
  auto-incrementing counter (`Team.NextIssueNumber`).
- `Issue.Source` (`IntegrationProvider`) plus an `ExternalLink` row is how a synced issue is
  traced back to its remote origin and de-duplicated on repeated syncs; issues created directly
  (by a human or an agent) are `IntegrationProvider.Local` with no `ExternalLink`.
- `IssueStatus`: `Backlog(0) → Todo(1) → InProgress(2) → InReview(3) → Done(4)`, plus
  `Cancelled(5)`. Deliberately a fixed enum, not a configurable workflow — see
  [Non-goals](#goals-and-non-goals).
- `IssuePriority`: `None(0), Low(1), Medium(2), High(3), Urgent(4)`.
- `IntegrationProvider`: `Local(0), GitHub(1), Linear(2), Custom(99)`. Third-party plugins use
  `Custom` and identify themselves via `ExternalLink.SourceKey`, not by extending this enum.

## REST API

All under `Anvilboard.Api`, minimal-API style, one `Endpoints` file per resource group:

| Route | Notes |
|---|---|
| `GET /api/teams` | List teams. |
| `POST /api/teams` | Create a team (`{ name, key }`). |
| `GET /api/members` | List members. |
| `POST /api/members` | Create a member (`{ displayName, email?, isAgent? }`). |
| `GET /api/issues?teamId&status&assigneeId` | List/filter issues. |
| `GET /api/issues/{id}` | Get one issue. |
| `POST /api/issues` | Create (`{ teamId, title, description?, priority?, projectId?, assigneeId? }`). |
| `PATCH /api/issues/{id}/status` | `{ status }` — numeric enum value. |
| `PATCH /api/issues/{id}/assignee` | `{ assigneeId }` (nullable, to unassign). |
| `POST /api/issues/{id}/comments` | `{ body, authorId? }`. |
| `GET /api/dashboard/summary?teamId` | Aggregate counts, see below. |
| `POST /webhooks/{provider}` | Dispatches to the `IWebhookReceiver` whose `RoutePrefix` matches `provider`. |

Enums are serialized as **plain JSON integers** on the wire (`ConfigureHttpJsonOptions` does not
register a `JsonStringEnumConverter`), matching this codebase's general REST/HTTP convention.
`Dictionary<TEnum, TValue>` values (used in the dashboard summary) serialize with the **enum
name** as the JSON key regardless of this setting — that's `System.Text.Json`'s default
dictionary-key behavior, confirmed against a live response:

```json
{
  "issuesByStatus": { "Backlog": 0, "Todo": 0, "InProgress": 1, "InReview": 0, "Done": 0, "Cancelled": 0 },
  "issuesBySource": { "Local": 1 },
  "createdLast7Days": 1,
  "completedLast7Days": 0,
  "openIssuesByAssignee": []
}
```

> **Deliberate inconsistency:** `Anvilboard.Agent`'s CLI/MCP surface registers its *own*, separate
> `JsonSerializerOptions` with `JsonStringEnumConverter` added, so a human typing
> `--status InProgress` or an agent's MCP tool call with `"status": "InProgress"` both work
> (numeric values still work too). This only affects the Agent process's own JSON
> binding/serialization; it has no effect on the Api's wire format, since they are two
> independently-configured `JsonSerializerOptions` instances in two different processes.

## Agent surface (CLI/MCP)

`Anvilboard.Agent` is built on [`dotnet-agent-surface`](../dotnet-agent-surface), which derives
both a CLI and an MCP tool server from one set of `[AgentOperation]`-annotated methods —
[`BoardAgentService`](src/Anvilboard.Agent/BoardAgentService.cs) — so the operation surface is
declared exactly once:

| Operation | Category | Wraps |
|---|---|---|
| `list-issues` | `issues` | `IssueService.ListAsync` |
| `get-issue` | `issues` | `IssueService.GetAsync` |
| `create-issue` | `issues` | `IssueService.CreateAsync` |
| `change-issue-status` | `issues` | `IssueService.ChangeStatusAsync` |
| `assign-issue` | `issues` | `IssueService.AssignAsync` |
| `comment-on-issue` | `issues` | `IssueService.AddCommentAsync` |
| `dashboard-summary` | `dashboard` | `DashboardService.GetSummaryAsync` |

Two run modes, chosen by `args[0]`:

- **CLI** (default): `anvilboard-agent <category> <operation> --flag value [--full]`. One-shot
  process; exits after the call. Example: `anvilboard-agent issues change-issue-status --issueId
  <guid> --status InProgress`.
- **MCP** (`anvilboard-agent mcp`): starts a stdio JSON-RPC server
  (`ModelContextProtocol`/`McpOperationServer`) exposing the same 7 operations as MCP tools with
  generated JSON schemas, for a coding agent to call directly. `SyncCoordinator` (the ingestion
  polling loop) is only started in this mode, since it's the only mode with a process lifetime
  long enough to make polling meaningful.

Both modes reuse the same DI-registered `IssueService`/`DashboardService` the Api host uses — an
agent creating an issue via MCP produces exactly the entity a human creates via the UI, with the
same validation, the same `ActivityEvent`/hook dispatch, and the same key-numbering scheme.

**Implementation notes**
- Log output is routed to **stderr only** in MCP mode — stdout is reserved exclusively for
  JSON-RPC traffic per the MCP stdio transport contract.
- The Agent's application services are `Scoped` in DI, but `OperationInvoker` is built once and
  reused across every CLI/MCP call; a small `ScopedServiceProvider : IServiceProvider` wrapper
  creates a fresh `IServiceScope` per operation invocation to mirror ASP.NET Core's per-request
  scoping without a request pipeline.

## Plugin system

See [`PLUGINS.md`](PLUGINS.md) for the full authoring guide. Summary of the shape:

- **`IIngestionSource`** — pull/poll. `SyncCoordinator` runs one independent timer loop per
  registered source and upserts yielded `NormalizedIssue`s via
  `IssueService.UpsertFromExternalAsync`, keyed on `(Provider, SourceKey)`.
- **`IWebhookReceiver`** — push. Mounted at `POST /webhooks/{RoutePrefix}`; the plugin owns
  signature verification and payload parsing entirely.
- **`IIssueHook`** — reactive, fire-and-forget, post-commit, best-effort (errors logged not
  thrown, hooks run concurrently, none can block or veto a mutation).

Plugins reach the host's `IPluginRegistry` two ways: compiled-in via
`services.AddSingleton<IAnvilboardPlugin>(...)` in an `AddXyzIntegration` extension method (how
GitHub/Linear are wired), or as an out-of-repo DLL listed in `Plugins:AssemblyPaths` and loaded by
reflection (how a private plugin, e.g. a Slack ticket-creation library, is enabled with zero
changes to this repository).

GitHub and Linear-style tracking are **first-class only in the sense that their integration
projects ship in this repository** — architecturally they are ordinary plugins against the same
three interfaces everything else uses.

## Web client

`anvilboard-web` is an Angular 22 standalone-component SPA (`ng new ... --style=scss
--routing=true --ssr=false`).

- **Build output wiring**: `angular.json`'s production `outputPath` is
  `{ base: "../Anvilboard.Api/wwwroot", browser: "" }`, so `ng build` writes `index.html` and
  chunks directly into the Api host's static-file root (no nested `browser/` subfolder) — the Api
  host's `UseStaticFiles()`/`MapFallbackToFile("index.html")` need no extra configuration to find
  them.
- **Dev workflow**: `ng serve` proxies `/api` and `/webhooks` to `http://localhost:5089` via
  `proxy.conf.json`, so the Api host runs standalone during frontend development.
- **Structure**:
  - `core/models.ts` — TypeScript mirrors of the domain enums/records (numeric enum values kept
    in lockstep with `Anvilboard.Domain`).
  - `core/board-api.service.ts` — one flat `HttpClient` wrapper over the whole REST surface,
    intentionally mirroring `BoardAgentService`'s operation set one-to-one so the web client and
    an agent exercise the same underlying capabilities.
  - `shell/app-shell` — left rail (brand, Board/Dashboard nav, team list) + router outlet.
  - `board/board-page`, `board/issue-card`, `board/issue-detail` — the Kanban board: one column
    per `IssueStatus`, inline quick-create per column, a slide-in detail panel for status changes,
    assignee display, and comments.
  - `dashboard/dashboard-page` — status/source breakdown bars, 7-day created/completed counts,
    open-issue load per assignee.
- **Visual design**: a deliberately minimal, dark, keyboard-and-craftsmanship-flavored theme
  (`styles.scss` design tokens: `--ab-bg`, `--ab-accent`, etc.) chosen to evoke the fast,
  no-ceremony feel of modern issue trackers — built from scratch as an original visual design,
  without reusing any specific product's name, wordmark, logo, or copied assets.

## Data flow: from a remote ticket to a board card

1. A first-class integration (`Anvilboard.Integrations.GitHub`/`.Linear`) or a third-party plugin
   implements `IIngestionSource` (polled) and/or `IWebhookReceiver` (pushed).
2. Either path produces `NormalizedIssue`/`NormalizedComment` records — the plugin's *only* job is
   describing the remote item; it never touches the `Issue` entity.
3. `IssueService.UpsertFromExternalAsync` resolves the target `Team` by `TeamKey`, finds or
   creates the `Issue` by `(Provider, SourceKey)` via `ExternalLink`, and applies the normalized
   fields.
4. The write commits, an `ActivityEvent` (`SyncedFromExternal`, or `Created` for a first sync) is
   recorded, and every registered `IIssueHook` is fired concurrently and best-effort.
5. The issue is now indistinguishable, from the API/UI/Agent's point of view, from one created
   directly — it shows up on the board, in `dashboard-summary`, and via `list-issues`/`get-issue`.

## Testing performed

- Full solution build (`dotnet build Anvilboard.slnx`): 0 warnings, 0 errors.
- `Anvilboard.Agent` CLI mode: every operation exercised against a live SQLite database
  (`list-issues`, `get-issue`, `create-issue`, `change-issue-status` with both numeric and string
  enum input, `assign-issue`, `comment-on-issue`, `dashboard-summary`).
- `Anvilboard.Agent` MCP mode: raw JSON-RPC `initialize` → `notifications/initialized` →
  `tools/list` over stdio, verifying schema correctness for all 7 tools.
- `anvilboard-web` production build output verified to land correctly in
  `Anvilboard.Api/wwwroot` with `index.html` at the root (no extra path segment).
- End-to-end browser smoke test (Playwright, headless Chromium) against a running
  `Anvilboard.Api` instance: created a team/member/issue via the REST API, loaded the SPA,
  confirmed the issue card renders on the board with zero console/page errors, navigated to the
  dashboard, and confirmed the aggregate counts matched what was created.
