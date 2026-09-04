# Writing an Anvilboard plugin

Anvilboard's extension surface is deliberately small: three interfaces in
[`Anvilboard.Plugins.Abstractions`](src/Anvilboard.Plugins.Abstractions), no marketplace, no
plugin manifest schema beyond a single record. GitHub and Linear-style integrations are
implemented as ordinary plugins against this same surface — there is no special-cased "first
party" API. If you can implement one of the interfaces below, your plugin is a first-class
citizen too.

## The three extension points

| Interface | Direction | Use it to... |
|---|---|---|
| [`IIngestionSource`](src/Anvilboard.Plugins.Abstractions/IIngestionSource.cs) | pull (polled) | Periodically fetch items from a remote system and hand them to the board. |
| [`IWebhookReceiver`](src/Anvilboard.Plugins.Abstractions/IWebhookReceiver.cs) | push (webhook) | React immediately to an inbound HTTP callback instead of waiting for the next poll. |
| [`IIssueHook`](src/Anvilboard.Plugins.Abstractions/IIssueHook.cs) | reactive (fire-and-forget) | Run side effects after an issue is created/changed — notify Slack, push a status back upstream, etc. |

A single plugin type may implement more than one of these — the built-in GitHub integration
implements both `IIngestionSource` (polling) and `IWebhookReceiver` (the `issues` webhook event)
in separate classes that share configuration.

All three extend the marker interface `IAnvilboardPlugin`, which requires only:

```csharp
public interface IAnvilboardPlugin
{
    PluginManifest Manifest { get; } // (Key, DisplayName, Version)
}
```

`Manifest.Key` is a stable, lowercase identifier (`"github"`, `"linear"`, `"slack-tickets"`) used
as:
- the configuration section name (`Plugins:<Key>`)
- the namespace prefix for dedupe/external-link keys
- the log line printed when the plugin is discovered

## 1. Pulling data in: `IIngestionSource`

```csharp
public interface IIngestionSource : IAnvilboardPlugin
{
    IAsyncEnumerable<NormalizedIssue> SyncAsync(SyncCursor cursor, CancellationToken cancellationToken);
}
```

Implement `SyncAsync` to yield `NormalizedIssue` records for anything new or changed since
`cursor` (an opaque, plugin-owned token — a "since" timestamp or a page cursor, your choice).
The host's `SyncCoordinator` runs one independent polling loop per registered source (interval
from `IngestionOptions.PollInterval`, bound from `Plugins:<Key>` config) and upserts whatever you
yield via `IssueService.UpsertFromExternalAsync`, keyed on `(Provider, SourceKey)`. You never
touch the `Issue` entity directly — describe the remote item, the host does the merge.

```csharp
public sealed record NormalizedIssue(
    IntegrationProvider Provider,   // Local | GitHub | Linear | Custom
    string SourceKey,               // e.g. "owner/repo#123" — dedupe key within Provider
    string TeamKey,                 // which local Team this maps to
    string Title,
    string? Description,
    IssueStatus SuggestedStatus,
    IssuePriority SuggestedPriority,
    string? Url,
    string? AssigneeEmail,
    IReadOnlyList<string> LabelNames,
    string? SyncFingerprint,
    DateTimeOffset RemoteUpdatedAt);
```

Third-party plugins (anything that isn't GitHub or Linear) should use
`IntegrationProvider.Custom` — you don't need to touch the `IntegrationProvider` enum to add a
new source; `SourceKey` is where your plugin's identity actually lives.

## 2. Pushing data in: `IWebhookReceiver`

```csharp
public interface IWebhookReceiver : IAnvilboardPlugin
{
    string RoutePrefix { get; } // mounted at POST /webhooks/{RoutePrefix}
    Task<WebhookResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken);
}
```

The API host exposes exactly one dynamic route, `POST /webhooks/{provider}`
([`WebhookEndpoints`](src/Anvilboard.Api/Endpoints/WebhookEndpoints.cs)), and dispatches to
whichever registered receiver's `RoutePrefix` matches. Everything provider-specific — signature
verification, payload parsing — lives inside your plugin; the host only sees
`WebhookRequest(Headers, RawBody)` and your `WebhookResult`.

Return `WebhookResult.Reject("reason")` for an invalid signature or unparseable payload (this
maps to `400`, not `500` — don't throw for expected validation failures). Return
`WebhookResult.Accept(issues, comments)` with any `NormalizedIssue`/`NormalizedComment`s the
delivery produced; the host upserts them the same way a poll result would be upserted.

## 3. Reacting to changes: `IIssueHook`

```csharp
public interface IIssueHook : IAnvilboardPlugin
{
    Task OnIssueChangedAsync(IssueHookContext context, CancellationToken cancellationToken);
}
```

Called once per persisted `ActivityEvent` — `Created`, `StatusChanged`, `AssigneeChanged`,
`PriorityChanged`, `CommentAdded`, `LabelsChanged`, or `SyncedFromExternal` — **after** the write
has already committed. Filter on `context.Event.Type` for the transitions you care about. Hooks:

- run **concurrently** with every other registered hook (`Task.WhenAll`)
- **cannot veto or mutate** the change that triggered them — there is no synchronous/blocking
  hook, by design, so a slow or misbehaving plugin can never stall the UI or an agent's mutation
- have exceptions **logged, not propagated** — one broken hook never breaks another plugin or the
  mutation itself

This is the extension point for "post a Slack message when an issue moves to In Review" or "push
a status change back to the originating GitHub issue" — anything that reacts to board activity
without needing to be in the write's critical path.

## Wiring a plugin into the host

There are two ways a plugin's instances end up in `IPluginRegistry.All`
([`PluginRegistry`](src/Anvilboard.Infrastructure/Plugins/PluginRegistry.cs)):

### In-repo / first-class (compiled in, like GitHub and Linear)

Add a `AddXyzIntegration(IServiceCollection, IConfiguration)` extension method that registers your
plugin type(s) as `IAnvilboardPlugin`, and call it from `Anvilboard.Api`'s and
`Anvilboard.Agent`'s `Program.cs` alongside the existing integrations.

```csharp
services.AddSingleton<MyIngestionSource>();
services.AddSingleton<IAnvilboardPlugin>(sp => sp.GetRequiredService<MyIngestionSource>());
```

> **Gotcha, learned the hard way:** register `IAnvilboardPlugin`, **not**
> `IIngestionSource`/`IWebhookReceiver` directly, if more than one plugin might be present.
> `IServiceProvider.GetRequiredService<T>()` only ever resolves the *last*-registered
> implementation of `T` — registering two plugins both as `IIngestionSource` makes
> `PluginRegistry`'s constructor (which takes `IEnumerable<IAnvilboardPlugin>`, not
> `IEnumerable<IIngestionSource>`) silently collapse to whichever was registered last. Register the
> concrete type as itself (for your own DI needs, e.g. an `HttpClient`) and *separately* register
> it again as `IAnvilboardPlugin` — see
> [`Anvilboard.Integrations.GitHub/ServiceCollectionExtensions.cs`](src/Anvilboard.Integrations.GitHub/ServiceCollectionExtensions.cs)
> for the pattern this project settled on after hitting exactly this bug.

### Out-of-repo / private (a separate DLL, e.g. a private Slack ticket-creation plugin)

Build a class library against `Anvilboard.Plugins.Abstractions` only (no reference to any other
Anvilboard project required), implement one or more of the three interfaces with a public
parameterless-or-DI-injectable constructor, and drop the built DLL path into configuration:

```json
{
  "Plugins": {
    "AssemblyPaths": ["C:/path/to/MyCompany.Anvilboard.SlackTickets.dll"]
  }
}
```

`PluginRegistry` loads each listed assembly with `Assembly.LoadFrom`, reflects over its public
non-abstract classes implementing `IAnvilboardPlugin`, and constructs each one via
`ActivatorUtilities.CreateInstance` — so constructor parameters are resolved from the host's DI
container exactly like any other service (you can inject `HttpClient`, `ILogger<T>`,
`IOptions<T>`, etc.). A failure loading one assembly is logged and skipped; it never prevents the
rest of the host or other plugins from starting.

This path requires **no code change or recompilation of Anvilboard itself** — it's how a private
lib (kept in a separate, possibly closed-source repo) becomes a board source.

## Per-plugin configuration

Ingestion polling is controlled per-plugin via `Plugins:<Key>`, bound to `IngestionOptions`:

```json
{
  "Plugins": {
    "github": { "Enabled": true, "PollInterval": "00:05:00" },
    "slack-tickets": { "Enabled": true, "PollInterval": "00:01:00" }
  }
}
```

Your plugin can bind its own richer options type from the same section (see
[`GitHubOptions`](src/Anvilboard.Integrations.GitHub/GitHubOptions.cs)) for provider-specific
settings (API tokens, repo lists, webhook secrets, ...) alongside the common `Enabled`/
`PollInterval` fields.

## Same surface for humans and agents

Nothing above is specific to the web UI. `Anvilboard.Agent`'s CLI/MCP surface
(`BoardAgentService`) calls the exact same `IssueService`/`DashboardService` that
`Anvilboard.Api`'s HTTP endpoints call, which is the same service every ingestion source and
webhook receiver upserts through — a plugin author never needs to think about whether an issue
originated from a person clicking a button, an agent invoking an MCP tool, or an inbound webhook.
