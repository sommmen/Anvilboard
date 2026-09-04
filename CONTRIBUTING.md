# Contributing to Anvilboard

Thanks for considering a contribution. Anvilboard is young and deliberately small in scope — the
fastest way to get a change merged is to keep it aligned with that scope and the existing
architecture rather than introducing a new one.

## Before you start

- **Read the specs first.** [FUNCTIONAL_SPEC.md](FUNCTIONAL_SPEC.md) covers what the product is
  for, [SPEC.md](SPEC.md) covers how it's built, and [DEVELOPMENT.md](DEVELOPMENT.md) covers how
  to build/run it locally. Most "is this the right approach" questions are already answered there.
- **Prefer a plugin over a core change.** If what you want is a new way to pull work in (another
  issue tracker, a chat tool, an internal system) or a side effect after issues change (another
  notification target), that almost certainly belongs in a new plugin against
  `Anvilboard.Plugins.Abstractions`, not a change to `Anvilboard.Application`, `Anvilboard.Api`, or
  `Anvilboard.Agent`. See [PLUGINS.md](PLUGINS.md). Plugins can live in their own private repo and
  never need to be contributed back here at all — `Plugins:AssemblyPaths` loads them from outside
  the tree.
- **Core changes** (new REST endpoints, new domain fields, new agent operations, UI changes) are
  welcome when they benefit the product generally rather than one integration's needs. If you're
  not sure whether something is "core" or "should be a plugin," open an issue describing the use
  case before writing code.

## Making a change

1. Fork/branch from `main` (once the repo has a `main` — see the note in [README.md](README.md)
   about the repository not being under version control yet at time of writing).
2. Keep changes to `Anvilboard.Application` in sync across both consumers: if you add a capability
   there, expose it from *both* `Anvilboard.Api`'s endpoints and `Anvilboard.Agent`'s
   `BoardAgentService` in the same change, so the web UI and agents never fall out of sync with
   each other. This is a hard rule, not a suggestion — it's the whole point of the architecture
   (see [SPEC.md](SPEC.md#solution-layout)).
3. Match existing conventions — see [DEVELOPMENT.md's coding conventions](DEVELOPMENT.md#coding-conventions)
   section (nullable enabled, minimal APIs, strongly-typed IDs, application-layer-first, standalone
   Angular components with signals).
4. Build before you open a PR:
   ```powershell
   dotnet build Anvilboard.slnx
   cd src/anvilboard-web && npm run build
   ```
5. There's no automated test suite yet (see [DEVELOPMENT.md's testing section](DEVELOPMENT.md#testing)).
   Describe what you manually verified in your PR description — which UI flows you clicked
   through, which CLI/MCP operations you called, which endpoints you hit. If your change is
   substantial, adding tests for the parts of `Anvilboard.Application` you touched is a
   particularly welcome addition on top of the fix/feature itself.
6. Keep pull requests scoped to one change. A plugin addition, a core feature, and a doc fix are
   three PRs, not one.

## Reporting bugs / proposing features

Open an issue describing:
- what you expected vs. what happened (for bugs), or
- the use case and why it needs a core change rather than a plugin (for features).

## Documentation changes

Documentation lives at the repo root (`README.md`, `FUNCTIONAL_SPEC.md`, `SPEC.md`, `PLUGINS.md`,
`DEVELOPMENT.md`, this file, `CHANGELOG.md`). Keep facts (ports, config keys, operation names)
consistent with the actual code — when in doubt, check the source rather than another doc, since
docs can drift.

## Code of conduct

Be respectful and assume good faith. Anvilboard doesn't have a separate formal code of conduct
document yet; treat this section as the baseline until one is added.
