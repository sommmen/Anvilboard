# Anvilboard — functional specification

This document describes Anvilboard from the outside: who it's for, what they can do with it, and
what it deliberately does not try to do. For how it's built, see [SPEC.md](SPEC.md).

## Problem statement

Work that matters to a team or a person building software is scattered: some of it is a GitHub
issue, some of it lives in a separate issue tracker, some of it is a Slack message that really
should have been a ticket, and increasingly some of it is a task an agent produced while working
on something else. None of these systems agree with each other, and standing up a "real" tracker
to unify them means hosting a SaaS product against your own data, or running an infrastructure
stack, just to get a single board.

Anvilboard exists to be the "one board" — a place all of that work lands, regardless of where it
came from, that you can run next to whatever else you're already running, own the data for, and
extend cheaply when a new source of work shows up.

## Who it's for

- **A developer or small team** who wants one task board that already reflects what's open on
  GitHub and their tracker, without manually copying tickets over.
- **Someone running coding agents** who wants agent-originated work to land on the same board a
  human sees, and wants agents to be able to read/update that board themselves rather than only
  reporting results in a chat transcript.
- **A plugin author** (in-repo or private) who wants to pull in a new source of work — Slack
  threads, an internal tool, an email inbox — without forking or modifying Anvilboard's core.

## User stories

1. **As a developer**, I open the board and see a Kanban view of everything assigned to my team,
   regardless of whether it originated on GitHub, a tracker, or was typed directly into Anvilboard.
2. **As a developer**, I create an issue directly on the board when the work doesn't come from
   anywhere else yet.
3. **As a developer**, I move an issue across the board (Backlog → Todo → In Progress → In Review
   → Done/Cancelled) and see that reflected immediately; no separate "sync" step.
4. **As a developer**, I open an issue and add a comment, see its priority, and see who it's
   assigned to.
5. **As a team lead**, I open the dashboard and see, at a glance: how much got created vs.
   completed in the last 7 days, how work breaks down by status and by source system, how many
   distinct people contributed, and who currently has open load.
6. **As someone connecting GitHub**, I configure a token and a list of repositories once, and
   issues opened there start appearing on the board on the next poll (or immediately, if a webhook
   is configured), tagged with their originating repository.
7. **As someone connecting a Linear-style tracker**, I configure an API key and team key(s), and
   issues from there start appearing the same way, mapped onto a local team.
8. **As an agent**, I list open issues for a team, create a new issue for a task I just discovered,
   change an issue's status as I complete work on it, and add a comment documenting what I did —
   using the same CLI/MCP surface a human would script against, without a bespoke integration.
9. **As a plugin author**, I write a small library against three interfaces
   (`IIngestionSource`/`IWebhookReceiver`/`IIssueHook`) to turn, say, a Slack message with a
   particular reaction into a new issue, and drop the compiled plugin into Anvilboard's plugin
   directory — without touching Anvilboard's source.
10. **As anyone running it**, I can stop the process, copy one `.db` file somewhere else (backup,
    another machine), and be running again with full history — no export/import step.

## Functional scope

### Task board

- Columns: **Backlog, Todo, In Progress, In Review, Done, Cancelled** — a fixed set matching
  `IssueStatus`, not user-configurable per team.
- Each card shows: issue key (e.g. `ENG-142`), source badge (Local/GitHub/Linear-style/Custom),
  title, priority glyph, and assignee initials.
- Quick-create: type a title into a column and press enter to create an issue directly in that
  status.
- Clicking a card opens an issue detail panel: full description, status-change controls,
  assignee, and a comment thread (add + view comments added in the current session — see
  [SPEC.md's API note](SPEC.md#rest-api) on the current lack of a "list comments" endpoint).
- Filterable by team (left-rail team list); assignee/status filtering is available through the API
  today and is a natural near-term UI addition (see [Out of scope / not yet built](#out-of-scope-for-v1--not-yet-built)).

### Dashboard

- **Stat cards:** issues created in the last 7 days, issues completed in the last 7 days, distinct
  contributors.
- **Status breakdown:** horizontal bar chart, issue count per `IssueStatus`.
- **Source breakdown:** horizontal bar chart, issue count per `IntegrationProvider` (how much of
  the board is Local vs. pulled from GitHub vs. pulled from a tracker vs. a custom plugin).
- **Open load by assignee:** list of members with their count of non-terminal (not Done/Cancelled)
  issues, so a lead can see who's carrying the most open work.

### Ingesting remote work

- **GitHub** (first-class, in-repo): polls configured repositories on an interval and/or reacts to
  an `issues` webhook event; maps a GitHub issue to a local issue, tagging it `IntegrationProvider.GitHub`
  and recording an `ExternalLink` so repeated syncs update rather than duplicate.
- **Linear-style tracker** (first-class, in-repo): same shape — polls configured teams and/or
  reacts to a webhook, tagging `IntegrationProvider.Linear`.
- **Anything else** (Slack, an internal tool, a different tracker, agent push): implemented as a
  plugin against the same three interfaces GitHub/Linear use — see [PLUGINS.md](PLUGINS.md). A
  private, closed-source plugin (e.g. a Slack ticket-creation library) is supported without
  Anvilboard ever depending on its source.

### Agent access

- Every board operation available to a human through the UI/REST API (list issues, get an issue,
  create an issue, change its status, change its assignee, comment on it, read the dashboard
  summary) is available to an agent through the same underlying application service, exposed as:
  - a **one-shot CLI** (`dotnet run -- list-issues ...`), good for scripting and quick agent tool
    calls, and
  - an **MCP server** (`dotnet run -- mcp`), good for an MCP-aware agent host holding a
    long-running session.
- Agents never get a reduced or "read-only" view by default — they use the identical
  `IssueService`/`DashboardService` calls a human's request eventually reaches, so there's exactly
  one code path to trust, not two that can drift apart.

### Hooks and events for plugin authors

- `IIssueHook` fires after an issue is created or changes, for side effects: notify a chat
  channel, push a status back to the originating system, trigger some other workflow. See
  [PLUGINS.md](PLUGINS.md) for the exact hook points and ordering guarantees (none — hooks run
  independently and a failing hook does not roll back the issue write).

## Non-functional requirements

- **Resource footprint:** must run comfortably alongside other local development tools on a
  laptop. One .NET process, one SQLite file, no required container orchestration. A production
  publish is a self-contained folder you can run from a USB stick if you wanted to.
- **Startup:** first run creates its schema automatically (`Database.MigrateAsync()` on startup);
  there's no separate "run migrations" step a new user has to know about.
- **Data ownership:** all data lives in one file on disk the operator controls; nothing is sent
  anywhere except the outbound calls a configured integration makes to fetch/post data at the
  operator's explicit request (a GitHub token you provided, a tracker API key you provided).
- **Consistency between surfaces:** the web UI, the REST API, and the agent CLI/MCP surface must
  never diverge in *what* they can do — enforced by construction, since they all call the same
  application-layer services.

## Out of scope for v1 / not yet built

These are explicitly not attempted yet, to keep the initial system small and coherent:

- Multi-tenant / hosted SaaS mode. Anvilboard is one instance, one operator, one SQLite file.
- Real-time collaborative editing (websockets, live cursors, CRDTs).
- User-configurable workflow states — the status enum is fixed.
- Authentication/authorization / multi-user permission model. Anvilboard currently assumes a
  trusted local/LAN operator; adding auth is a natural next step before exposing it beyond
  localhost.
- A "list comments" REST endpoint (comments can be added via API/UI; only the current API-listed
  issue payload plus session-added comments are shown in the panel today).
- Board-level assignee/status filter controls in the UI (the API already supports the query
  parameters; wiring UI controls is straightforward follow-up work).
- A plugin marketplace/discovery UI — plugins are wired via configuration
  (`Plugins:AssemblyPaths`), not installed through the product itself.

## Glossary

| Term | Meaning |
|---|---|
| Issue | A unit of work, whether created locally or synced from a remote source. |
| Team | Owns a namespace of issue keys (e.g. `ENG-`) and a set of members. |
| Source / provider | Where an issue originated: Local, GitHub, a Linear-style tracker, or Custom (any other plugin). |
| Ingestion source | A plugin that polls a remote system for new/changed items. |
| Webhook receiver | A plugin that reacts immediately to an inbound HTTP callback from a remote system. |
| Issue hook | A plugin that runs a side effect after an issue is created or changed. |
| Agent surface | The CLI + MCP interface that lets a coding agent operate the board like a human would. |
