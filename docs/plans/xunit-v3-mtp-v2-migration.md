# Implementation Plan: migrate tests to `xunit.v3.mtp-v2` 4.0.1

Migrates all seven test projects from xUnit v2 on VSTest to xUnit v3 on Microsoft
Testing Platform v2, aligning this repository with the estate-wide test standard.

The shared standard, target recipe and CI command reference live in the `stallions`
repository at `docs/guides/Testing/xunit-v3-mtp-v2-standard.md`.

## 1. Document information

| Field | Value |
|---|---|
| Document | Implementation Plan — xUnit v3 / MTP v2 test migration |
| Type | Cross-cutting infrastructure; no product behaviour change |
| Scope | 7 test projects, ~70 test source files, 503 `[Fact]` + 21 `[Theory]` |
| Status | **Not started** |
| Created | 2026-10-09 |

## 2. Why

This repository runs xUnit v2 2.9.3 with the VSTest host. The estate standard is
xUnit v3 on Microsoft Testing Platform v2 via `xunit.v3.mtp-v2` 4.0.1. The framework
API used by ordinary tests is nearly unchanged between v2 and v3, so the cost here is
packaging and one real breaking change, not a test rewrite.

## 3. Current state

All seven projects share an identical package set:

| Package | Version | Fate |
|---|---|---|
| `xunit` | 2.9.3 | → `xunit.v3.mtp-v2` 4.0.1 |
| `xunit.runner.visualstudio` | 4.0.0 | remove (VSTest adapter) |
| `Microsoft.NET.Test.Sdk` | 18.9.0 | remove (VSTest host) |
| `coverlet.collector` | 10.0.1 | remove (VSTest collector) |

| Project | Extra packages |
|---|---|
| `src/Anvilboard.Agent.Tests` | — |
| `src/Anvilboard.Api.Tests` | `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, `Microsoft.AspNetCore.SignalR.Client` 10.0.11 |
| `src/Anvilboard.Application.Tests` | — |
| `src/Anvilboard.Infrastructure.Tests` | — |
| `src/Anvilboard.Integrations.GitHub.Tests` | — |
| `src/Anvilboard.Integrations.Linear.Tests` | — |
| `tests/Anvilboard.IntegrationTests` | — |

All target `net10.0`, all set `ImplicitUsings`, `Nullable`, `IsPackable=false` and
`<Using Include="Xunit" />`. None sets `OutputType` — correct for v2, wrong for v3.
There is no `Directory.Build.props`, no `Directory.Packages.props` and no `global.json`;
every version is pinned per project file.

Solution: `Anvilboard.slnx`. CI: `.github/workflows/ci.yml`, job
`build-and-test-dotnet`.

### Migration surface, measured

| Construct | Count | Impact |
|---|---:|---|
| `[Fact]` | 503 | none |
| `[Theory]` | 21 | none |
| `[InlineData]` | 78 | none |
| `Assert.ThrowsAsync` | 97 | none |
| `IAsyncLifetime` | 3 | **breaking — see §4.1** |
| `async void` | 0 | none |
| `ITestOutputHelper` | 0 | none |
| `IClassFixture` / `ICollectionFixture` | 0 | none |

The overwhelming majority of the ~70 test files need no source change at all.

## 4. The one real breaking change

### 4.1 `IAsyncLifetime` now returns `ValueTask`

Three classes implement `IAsyncLifetime`:

- `src/Anvilboard.Application.Tests/Backup/BackupRoundTripTests.cs:24`
- `src/Anvilboard.Application.Tests/Backup/BackupSecretScanTests.cs:23`
- `src/Anvilboard.Application.Tests/Backup/BackupServiceTests.cs:24`

In v3 the interface inherits `IAsyncDisposable`, so both members return `ValueTask`:

```csharp
// before (v2)
public Task InitializeAsync() => Task.CompletedTask;
public Task DisposeAsync() => Task.CompletedTask;

// after (v3)
public ValueTask InitializeAsync() => ValueTask.CompletedTask;
public ValueTask DisposeAsync() => ValueTask.CompletedTask;
```

This is a **compile** error, not a silent behaviour change — verified to produce
`CS0738` on both members — so it cannot be missed. All three classes are `sealed` and
none also implements `IDisposable`, so the "v3 calls `DisposeAsync` or `Dispose`, never
both" rule does not apply here.

Check whether any of the three currently has an `async Task InitializeAsync` body with
real awaits; `async ValueTask` works identically, so only the return type changes.

## 5. Changes

### 5.1 Introduce a test-scoped `Directory.Build.props` (recommended)

Seven projects need the same two new properties. Rather than repeating them, add
`src/Directory.Build.props`-style files scoped to the test projects. Because the test
projects live in two different folders (`src/*.Tests/` and `tests/`), the simplest
correct option that avoids leaking `OutputType=Exe` into production libraries is a
condition on the project name:

```xml
<Project>
  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Tests'))">
    <OutputType>Exe</OutputType>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
</Project>
```

If a repository-root `Directory.Build.props` feels too broad, set both properties
directly in each of the seven project files instead. Do not set `OutputType=Exe`
unconditionally at the root — `Anvilboard.Domain`, `Anvilboard.Application` and the
other libraries must stay libraries.

### 5.2 Each of the seven test project files

Remove `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` and
`coverlet.collector`. Add:

```xml
<PackageReference Include="xunit.v3.mtp-v2" Version="4.0.1" />
<PackageReference Include="Microsoft.Testing.Extensions.GitHubActionsReport" Version="2.4.1" />
```

Keep `<Using Include="Xunit" />`, all `ProjectReference`s, and the ASP.NET testing
packages in `Anvilboard.Api.Tests`.

`Microsoft.AspNetCore.Mvc.Testing` is framework-agnostic — it provides
`WebApplicationFactory<T>`, not a test framework — so it works unchanged with xUnit v3.

### 5.3 New `global.json`

At the repository root, next to `Anvilboard.slnx`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

The `test` block makes a bare `dotnet test` use MTP. The `sdk` block closes a real gap:
the SDK is currently pinned only in CI, so local builds use whatever is installed.

### 5.4 Coverage

`coverlet.collector` is a VSTest data collector with no MTP equivalent. Nothing in CI
consumes coverage output today, so drop it. If coverage is wanted later, add
`Microsoft.Testing.Extensions.CodeCoverage` 18.12.0 and run `dotnet test --coverage`.

### 5.5 CI

`.github/workflows/ci.yml` line 52 runs:

```
dotnet test Anvilboard.slnx --no-build --no-restore --configuration Release --verbosity normal
```

This keeps working under MTP unchanged. Optionally add `--report-gh` for GitHub
annotations and a run summary, which is why §5.2 adds the extension package.

Leave the `dotnet-agent-surface` sibling checkout alone — `Anvilboard.Agent` references
that repository by relative `ProjectReference`. Note the coupling: `dotnet-agent-surface`
is also migrating to `xunit.v3.mtp-v2`, but since the reference is to production
libraries rather than test projects, the two migrations are independent and can land in
either order.

## 6. Execution order

A single pull request is reasonable here, since the change is mechanical and the
compiler catches the one breaking construct. If splitting is preferred:

| # | Step |
|---|---|
| 1 | `src/Anvilboard.Agent.Tests` — smallest, no extra packages; proves the recipe |
| 2 | The four other `src/*.Tests` projects, including the `IAsyncLifetime` fixes in `Application.Tests` |
| 3 | `src/Anvilboard.Api.Tests` — the ASP.NET integration surface |
| 4 | `tests/Anvilboard.IntegrationTests`, then `global.json` and the CI flag |

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Zero discovery after migration | Medium | Missing `OutputType=Exe` is the cause; compare per-project test counts against the baseline |
| `IAsyncLifetime` signature | Certain | Compile error; fix the three files listed in §4.1 |
| `xUnit1051` warning volume | **Certain** | See below — advisory here, not blocking |
| Loss of coverage data | Low | Nothing consumes it today; §5.4 |

### `xUnit1051`

`xunit.v3.mtp-v2` 4.0.1 brings `xunit.analyzers` 2.1.0, which fires `xUnit1051`
("calls to methods which accept `CancellationToken` should use
`TestContext.Current.CancellationToken`"). A trial migration of a comparable repository
(546 tests) produced **150 of these warnings** and no other new rule. With 97
`Assert.ThrowsAsync` sites across ~70 files, expect a similar volume here.

This repository does **not** set `TreatWarningsAsErrors`, so the build still succeeds
and these are advisory. Land the migration first, then fix the sites as follow-up work —
passing `TestContext.Current.CancellationToken` genuinely improves cancellation
responsiveness, so it is worth doing, just not in the same commit as the framework swap.

## 8. Verification

```powershell
dotnet restore Anvilboard.slnx
dotnet build Anvilboard.slnx --configuration Release
dotnet test Anvilboard.slnx --no-build --configuration Release --verbosity normal
```

Definition of done:

1. No `xunit` (v2), `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` or
   `coverlet.collector` reference remains in the repository.
2. All seven projects reference `xunit.v3.mtp-v2` 4.0.1.
3. The total test count matches the pre-migration baseline — capture it with a full run
   before starting, and compare **per project**, since a single project silently
   dropping to zero is the main failure mode.
4. `ci` green on the pull request, including the unaffected Angular job.
