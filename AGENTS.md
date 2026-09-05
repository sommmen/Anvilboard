# Repository guidance

## Overview

Anvilboard is a local-first issue tracker and dashboard hosted in-process,
next to whatever else you're already running. It pulls work in from GitHub
issues, Linear-style trackers, Slack, or coding agents, and lands it on a
single task board and dashboard backed by one SQLite file — no SaaS account,
no Docker Compose stack, no separate database server.

`Anvilboard.Api` serves the REST API and the built Angular SPA from a single
executable (`dotnet run`, or one published binary, is the whole deployment).
See [README.md](README.md) for the full feature set and
[DEVELOPMENT.md](DEVELOPMENT.md) for local setup.

## Commit conventions

Use Conventional Commits for every commit message and pull request title:

```
<type>(<scope>): <description>
```

- `type` — one of `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`,
  `ci`, `perf`, `style`
- `scope` — the module, package, or area the change touches
- `description` — a short, imperative summary of the change

Examples:

- `feat(auth): add refresh token rotation`
- `fix(api): handle null response from upstream service`
- `chore(deps): bump dependency versions`
