# Anvilboard Product Direction

**Status:** Graduated to specification
**Last updated:** 2026-09-05

## Problem

Work for a software team is spread across local planning, GitHub, Linear, and automation agents. Each system has useful context, but no local, durable view that lets humans and agents coordinate without adopting a hosted work-management platform or treating a mirror as the system of record.

## Product thesis

Anvilboard is a local-first work coordination hub: a lightweight, self-hosted board that consolidates selected external work, preserves its provenance, and exposes the same governed operations to a web user and an AI agent.

## Target users

- Individual developers and small engineering teams who want local ownership and low operational overhead.
- AI-assisted development workflows that need a reliable planning and progress surface.
- Plugin authors who need to integrate a tracker, inbox, or notification destination without changing the core product.

## Direction

The existing proof of concept establishes the core: SQLite persistence, a fixed work-state model, GitHub and Linear ingestion, an Angular board, and an MCP/CLI surface. The product direction expands this into a dependable coordination system with workspace administration, searchable and filterable work, transparent synchronization, a supported plugin lifecycle, an auditable agent surface, and operational safeguards.

## Principles

1. **Local ownership by default.** Anvilboard must run with one deployable host and a local durable database; external services remain optional sources or destinations.
2. **One domain behavior.** Web, REST, CLI, and MCP callers use the same application services and authorization policy.
3. **Provenance is first-class.** A user can tell where work originated, when it synchronized, and which remote item it represents.
4. **Progressive sophistication.** The default board remains simple; advanced filters, reporting, integrations, and plugins do not make a first-run workflow difficult.
5. **Safe automation.** Agents can perform useful work with explicit identity, validation, audit events, and bounded tool contracts.

## Outcome

The canonical product requirements, requirements specification, technical design, and component specifications are maintained under [`docs/anvilboard`](../../docs/anvilboard/) and [`docs/features`](../../docs/features/).