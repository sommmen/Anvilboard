using Anvilboard.Api.Authorization;
using Anvilboard.Api.Endpoints;
using Anvilboard.Api.Middleware;
using Anvilboard.Api.Realtime;
using Anvilboard.Domain;
using Anvilboard.Domain.Serialization;
using Anvilboard.Application;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Realtime;
using Anvilboard.Infrastructure;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Integrations.GitHub;
using Anvilboard.Integrations.Linear;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory());
    options.SerializerOptions.Converters.Add(new UpperSnakeCaseEnumJsonConverterFactory());
});

builder.Services.AddOpenApi();

builder.Services.AddAnvilboardInfrastructure(builder.Configuration);
builder.Services.AddAnvilboardApplication();
builder.Services.AddAnvilboardSyncCoordinator();
builder.Services.AddGitHubIntegration(builder.Configuration);
builder.Services.AddLinearIntegration(builder.Configuration);

// Realtime: the bounded coalescing pipeline plus the SignalR transport that actually delivers.
// Registered before AddAnvilboardRealtime's TryAdd so the no-op transport is never selected here.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeTransport, SignalRRealtimeTransport>();
builder.Services.AddAnvilboardRealtime(builder.Configuration);

// REST resolves the correlation id from the inbound header so a mutation, its audit record, its log
// lines, and the realtime envelope it produces all carry the same value.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(provider => CorrelationContext.FromHeaderOrNew(
    provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers["X-Correlation-Id"]));

// Resolves route identifiers against the authenticated request's workspace. Scoped because it reads
// the per-request ActorContext the authorization middleware attached.
builder.Services.AddScoped<RestWorkspaceScope>();

var app = builder.Build();

// Apply any pending EF Core migrations on startup so a first-run `dotnet run` (or a single
// published executable) needs no separate migration step — the whole app, schema included, comes
// up from nothing but the executable and one SQLite file.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

// Serves the built Angular client (wwwroot, populated by the client's production build). Placed
// ahead of WorkspaceAuthorizationMiddleware because UseStaticFiles is terminal, physical-file
// middleware that never reaches endpoint routing (so it would otherwise be treated as an
// unauthenticated, endpoint-less request and rejected) — the SPA shell and its bootstrap/login
// screens must load before any credential exists.
app.UseDefaultFiles();
app.UseStaticFiles();

// Echoes the resolved correlation id on every response, including authentication/authorization
// denials, which is why it runs ahead of the enforcement point below.
app.UseMiddleware<CorrelationIdMiddleware>();

// Every route mapped after this point is authenticated by WorkspaceAuthorizationMiddleware (§11.2
// single enforcement point) unless it explicitly opts out with `.AllowAnonymous()` (bootstrap,
// login, webhooks, and the SPA fallback route below).
app.UseMiddleware<WorkspaceAuthorizationMiddleware>();

// Host-wide database-operation admission gate (plan §8.1/§8.4): every /api request leases itself
// with IRestoreCoordinator so a restore in flight can drain active work before its safety copy and
// file swap. Placed immediately after authentication/authorization so an unauthenticated or
// forbidden request is rejected on those grounds first, without consuming a lease.
app.UseMiddleware<DatabaseOperationMiddleware>();

app.MapAuthEndpoints();
app.MapIssueEndpoints();
app.MapArtifactEndpoints();
app.MapTeamEndpoints();
app.MapDashboardEndpoints();
app.MapWebhookEndpoints();
app.MapBackupEndpoints();
app.MapWorkflowEndpoints();

// Authorized by the same middleware as every REST route, so an unauthenticated client is refused
// during the negotiate/connect request itself and never observes an established connection. The hub
// re-resolves the actor in OnConnectedAsync to pick its workspace group (§11.2 stays the single
// enforcement point; the hub only reads the decision this middleware already made).
app.MapHub<WorkspaceRealtimeHub>(WorkspaceRealtimeHub.HubPath).RequirePermission(Permission.ReadBoard);

// Falls back to index.html for client-side routes so the whole product ships and runs as one
// process and one executable with no separate web server or reverse proxy in front of it.
// Anonymous: an unauthenticated client-side route (e.g. the login screen) must still resolve.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposed so WebApplicationFactory-based integration tests can bootstrap this host.
public partial class Program;
