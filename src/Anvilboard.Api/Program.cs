using Anvilboard.Api.Endpoints;
using Anvilboard.Domain.Serialization;
using Anvilboard.Application;
using Anvilboard.Infrastructure;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Integrations.GitHub;
using Anvilboard.Integrations.Linear;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory());
});

builder.Services.AddOpenApi();

builder.Services.AddAnvilboardInfrastructure(builder.Configuration);
builder.Services.AddAnvilboardApplication();
builder.Services.AddAnvilboardSyncCoordinator();
builder.Services.AddGitHubIntegration(builder.Configuration);
builder.Services.AddLinearIntegration(builder.Configuration);

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

app.MapIssueEndpoints();
app.MapTeamEndpoints();
app.MapDashboardEndpoints();
app.MapWebhookEndpoints();

// Serves the built Angular client (wwwroot, populated by the client's production build) and falls
// back to index.html for client-side routes, so the whole product ships and runs as one process
// and one executable with no separate web server or reverse proxy in front of it.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

// Exposed so WebApplicationFactory-based integration tests can bootstrap this host.
public partial class Program;
