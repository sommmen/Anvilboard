# Writing an Anvilboard plugin (historical PoC snapshot)

> **Superseded.** This document described the proof-of-concept's three-interface plugin surface
> (`IIngestionSource`, `IWebhookReceiver`, `IIssueHook`) only. It is retained for historical
> reference and is **not** updated as the product evolves. The canonical, actively maintained
> specification for the integration and plugin platform — including provider lifecycle, secret
> handling, provenance, sync health, and plugin fault isolation — is
> [`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md),
> grounded in [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) and
> [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) (`FR-INT-*`).

## What this document was

A guide for implementing one of three plugin interfaces in `Anvilboard.Plugins.Abstractions`:

- `IIngestionSource` — periodic polling of a remote system.
- `IWebhookReceiver` — reacting to an inbound HTTP webhook at `POST /webhooks/{provider}`.
- `IIssueHook` — reacting to a persisted `ActivityEvent` (created/status-changed/etc.) for side
  effects such as notifications.

It also covered wiring a plugin into the host (in-repo compiled-in vs. an out-of-repo DLL loaded
via `Plugins:AssemblyPaths`), per-plugin configuration under `Plugins:<key>`, and the principle
that the same three interfaces serve both first-party (GitHub, Linear-style) and third-party
plugins with no special-cased API.

## Why it was retired as the source of truth

The target product elevates integrations from "polled/webhook side inputs with no lifecycle" to
a first-class, auditable subsystem: connection lifecycle and status, credential/secret handling
without plaintext storage, external provenance guarantees, sync health as both product data and
an operational signal, and plugin fault isolation so one failing plugin cannot degrade the host.
Those requirements did not exist when this document was written, so the interface contracts and
operational guarantees now need a document that can express lifecycle state, error contracts, and
acceptance criteria — not just interface shapes.

## Where to look instead

| Question | Canonical document |
|---|---|
| What must the integration/plugin platform do (lifecycle, provenance, health, fault isolation)? | [`docs/anvilboard/srs.md`](docs/anvilboard/srs.md) `FR-INT-001`–`FR-INT-003` |
| How is it architected (adapters, secret provider abstraction, ingestion pipeline)? | [`docs/anvilboard/tech-design.md`](docs/anvilboard/tech-design.md) |
| How exactly do I implement/extend a provider or plugin today? | [`docs/features/integration-and-plugin-platform.md`](docs/features/integration-and-plugin-platform.md) |
| How is this verified? | [`docs/anvilboard/test-cases.md`](docs/anvilboard/test-cases.md) |

The underlying interfaces in `src/Anvilboard.Plugins.Abstractions` are still the current
extension point at the code level; the feature spec above is the up-to-date description of their
contract, lifecycle, and error handling.
