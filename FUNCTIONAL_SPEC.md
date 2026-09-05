# Anvilboard — functional specification (historical PoC snapshot)

> **Superseded.** This document described Anvilboard's proof-of-concept functional scope only —
> a fixed Kanban board (Backlog → Todo → In Progress → In Review → Done/Cancelled), an
> unauthenticated dashboard, and GitHub/Linear-style polling ingestion. It is retained for
> historical reference and is **not** updated as the product evolves.
>
> The canonical, actively maintained requirements are:
>
> - [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md) — product rationale, personas, goals,
>   priorities, and success measures.
> - [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) — formal functional and non-functional
>   requirements (`FR-*`/`NFR-*` IDs), acceptance criteria, and traceability.
> - [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) — how those requirements
>   are realized architecturally, including human/agent scenarios and measurable acceptance
>   criteria (`AC-001`–`AC-012`).

## What this document was

A description of Anvilboard "from the outside": who it was for, what a person or plugin author
could do with the proof of concept, and what it deliberately left out. It covered:

- **Who it was for:** a developer/small team wanting one board reflecting GitHub/tracker work, an
  agent operator wanting agents to read/write the same board a human sees, and a plugin author
  wanting to add a new work source.
- **Task board:** a fixed six-column Kanban view keyed to the `IssueStatus` enum, quick-create,
  an issue detail panel, and team filtering.
- **Dashboard:** unauthenticated stat cards and breakdowns (status, source, open load by
  assignee).
- **Ingesting remote work:** GitHub and Linear-style polling/webhook ingestion as first-class,
  in-repo plugins.
- **Agent access:** CLI/MCP operations calling the same application services as the REST API.
- **Explicitly out of scope for v1:** authentication/authorization, configurable workflows,
  real-time collaboration, and a generic workflow/BPM engine.

## Why it was retired as the source of truth

Every item in that "out of scope for v1" list is now a first-class requirement in the target
product: workspace-scoped authorization and roles, configurable workflow states/transitions,
integration provenance and sync health, audit trails, and backup/restore verification. Continuing
to maintain this document alongside the new requirements would mean either contradicting the new
scope or silently dropping the "what it does not do" framing that made it useful — both are worse
than pointing to the SRS and PRD, which now state both current commitments and explicit open
questions/non-goals in one place.

## Where to look instead

| Question | Canonical document |
|---|---|
| Who is this for, and why? | [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md), [`ideas/anvilboard/draft.md`](ideas/anvilboard/draft.md) |
| What can a user/agent do, precisely, and what are the acceptance criteria? | [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) §5 (Functional Requirements) |
| What are the non-functional expectations (performance, security, reliability)? | [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) §6 |
| What is explicitly out of scope or an open question? | [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md), [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) (open questions) |
| How does the board, workflow engine, integrations, or agent surface work in detail? | [`docs/features/`](docs/features/) — see [`overview.md`](docs/features/overview.md) |
| How to write a plugin | [`PLUGINS.md`](PLUGINS.md) (superseded pointer) → [`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md) |
