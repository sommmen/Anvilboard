# Anvilboard — technical specification (historical PoC snapshot)

> **Superseded.** This document described the architecture of the original proof-of-concept
> build only. It is retained for historical reference and is **not** updated as the product
> evolves. The canonical, actively maintained technical specification is
> [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md), which defines the target
> architecture (workspace-scoped authorization, configurable workflows, integration lifecycle,
> audit/recovery, and the REST/CLI/MCP contract) that this codebase is evolving toward.
>
> For requirements and product rationale, see [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md)
> and [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md). For per-component implementation
> detail, see the feature specs under [`docs/features/`](docs/features/), starting with
> [`docs/features/overview.md`](docs/features/overview.md).

## What this document was

Anvilboard's original as-built technical specification, written while the product was a
single-process, single-SQLite-file issue tracker with a fixed `IssueStatus` enum, no
workspace-scoped authorization, and three plugin interfaces (`IIngestionSource`,
`IWebhookReceiver`, `IIssueHook`). It described:

- The single-process hosting model (`Anvilboard.Api` serving the REST API and the built Angular
  SPA from one executable).
- The domain model as it existed then: `Workspace → Team → Issue`, a fixed `IssueStatus` enum,
  `IntegrationProvider` (`Local`, `GitHub`, `Linear`, `Custom`), and `ExternalLink`-based dedupe.
  Legacy `IssueStatus` is superseded by the configurable `WorkflowState`/`WorkflowTransition`
  model in the tech design — see its migration/compatibility section for how existing statuses
  map onto the new workflow model.
- The REST surface as it existed then (unauthenticated, unversioned routes under `/api/...`).
  The canonical technical design versions these under `/api/v1` with workspace scoping and
  authorization on every route — see its API section for current contracts.
- The CLI/MCP agent surface built on `dotnet-agent-surface`, calling the same application
  services as the REST API.

## Why it was retired as the source of truth

The proof of concept intentionally left out authorization, configurable workflows, integration
provenance/health, audit trails, backup/restore, and idempotent agent operations. The product
direction now treats those as first-class requirements rather than future nice-to-haves, so a
single as-built document could no longer describe both what exists and where the product is
going without becoming self-contradictory. The Spec-Forge chain
(idea → PRD → SRS → tech design → feature specs → test cases) replaces it with documents that
separate current requirements from implementation detail and keep stable requirement IDs for
traceability.

## Where to look instead

| Question | Canonical document |
|---|---|
| Why does Anvilboard exist, who is it for? | [`ideas/anvilboard/draft.md`](ideas/anvilboard/draft.md), [`docs/anvilboard/prd.md`](docs/anvilboard/prd.md) |
| What must the system do (functional/non-functional requirements)? | [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) |
| What is the target architecture, data model, API conventions, security, and operations design? | [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) |
| How does a specific component work (authorization, workflow engine, board, integrations, automation surface, audit/recovery)? | [`docs/features/`](docs/features/) — see [`overview.md`](docs/features/overview.md) |
| How is this verified? | [`docs/anvilboard/test-cases.md`](docs/anvilboard/test-cases.md) |
| How is the product decomposed into delivery streams? | [`docs/project-anvilboard.md`](docs/project-anvilboard.md) |

The as-built code today still matches most of what this file described (see
[`DEVELOPMENT.md`](DEVELOPMENT.md) for the current repository layout); the canonical documents
describe where that code is headed.
