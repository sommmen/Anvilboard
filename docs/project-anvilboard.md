FEATURE_MANIFEST
- workspace-and-board: docs/anvilboard/prd.md, docs/anvilboard/srs.md, docs/anvilboard/tech-design.md
- integration-and-plugin-platform: docs/features/integration-and-plugin-platform.md
- agent-and-automation-surface: docs/features/agent-and-automation-surface.md

# Project: Anvilboard

## Scope decision

**Multi-feature product.** Anvilboard is one product, but its roadmap has independently specifiable surfaces: work coordination, integration/plugin extensibility, and agent automation. They share a domain model, persistence, audit events, and deployment model; therefore the canonical PRD, SRS, and technical design describe the system as a whole, while component specifications define the delivery boundaries.

## Sub-features

### 1. Workspace and board

The human-facing coordination experience: workspace setup, teams, members, projects, issues, comments, labels, views, filters, reporting, backup, and audit visibility. This is the primary product surface and owns the canonical requirements and architecture documents.

### 2. Integration and plugin platform

The connector lifecycle for imports, inbound webhooks, outbound reactions, synchronization state, configuration, health, and extension packaging. It depends on the core issue and activity-event model but can be evolved as a distinct implementation stream.

### 3. Agent and automation surface

The CLI/MCP contract for agent discovery and mutations, agent identity, authorization, idempotency, auditability, and operational behavior. It depends on shared application services and must remain behaviorally consistent with the REST/UI surface.

## Execution order

1. Establish the workspace, issue, audit, and authorization foundations.
2. Deliver board operations, views, and reporting on those foundations.
3. Harden integration and plugin lifecycle behavior around the persisted core.
4. Expose and govern the identical operations through CLI/MCP automation.

## Cross-cutting concerns

- Single-host, local-first deployment and SQLite durability.
- Workspace-scoped access control, secrets handling, audit records, and backup/restore.
- API versioning, observability, structured errors, and migration compatibility.
- Provenance and synchronization transparency for every externally sourced item.

## Traceability

- Product direction: [`ideas/anvilboard/draft.md`](../ideas/anvilboard/draft.md)
- Product requirements: [`docs/anvilboard/prd.md`](anvilboard/prd.md)
- System requirements: [`docs/anvilboard/srs.md`](anvilboard/srs.md)
- Technical design: [`docs/anvilboard/tech-design.md`](anvilboard/tech-design.md)