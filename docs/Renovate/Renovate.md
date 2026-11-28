# Renovate

## Authentication

Renovate runs self-hosted from `.github/workflows/renovate.yml` and needs a
token that is **not** `GITHUB_TOKEN`: only another identity lets
Renovate-triggered changes start the workflow runs that automerge waits on.

This repository is user-owned, so an App cannot be installed org-wide and a
personal access token is used:

- `RENOVATE_TOKEN` — an Actions secret holding the PAT, passed to
  `renovatebot/github-action` as `token:`. There is no `GITHUB_TOKEN` fallback.

A PAT ties Renovate to one account: PRs appear as that user, and the setup
breaks when the user leaves or the token expires. The workflow's own
`permissions:` stay minimal (`contents: read`, `packages: read`) because the
PAT does the pushing.

### Validating the config

Run this command in the repo root, naming both files so the runner config is
checked too:

```powershell
npx --yes --package renovate -- renovate-config-validator renovate.json renovate-config.json
```

## Workflow triggers

The workflow runs on a daily cron, on `workflow_dispatch`, and on pushes to
`main` that touch certain paths. Get the path filter wrong and Renovate
silently stops reacting to the things that matter.

- **Watch both config files.** `renovate-config.json` is the runner config
  passed to the action via `configurationFile:`; `renovate.json` holds the
  `packageRules`. A filter listing only the runner config means editing a
  package rule never triggers a run until the next cron.
- **Watch the shared dependency manifests**: `Directory.Packages.props`,
  `**/dotnet-tools.json`, and `src/anvilboard-web/package.json` with
  `src/anvilboard-web/pnpm-lock.yaml`. When a dependency PR merges, every other
  open dependency PR is one commit behind `main`; a push trigger lets Renovate
  rebase them immediately instead of leaving them conflicted.
- **Queue, do not cancel.** `concurrency: {group: renovate,
  cancel-in-progress: false}`: an in-progress run may predate the merge that
  triggered the new one, so cancelling could discard the rebase.

### Automerging

For automerge to actually merge PRs, GitHub's auto-merge feature must be
enabled on the repository, and any protected branch must allow it.

- <https://docs.renovatebot.com/key-concepts/automerge/>
- <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-auto-merge-for-pull-requests-in-your-repository#managing-auto-merge>