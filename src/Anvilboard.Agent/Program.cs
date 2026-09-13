using System.Text.Json;
using Anvilboard.Agent;
using Anvilboard.Agent.Authorization;
using Anvilboard.Agent.Automation;
using Anvilboard.Agent.Hosting;
using Anvilboard.Application;
using Anvilboard.Domain.Serialization;
using Anvilboard.Infrastructure;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Integrations.GitHub;
using Anvilboard.Integrations.Linear;
using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Anvilboard.Agent is the CLI+MCP surface built on dotnet-agent-surface: it exposes the exact same
// application services (IssueService/DashboardService, via BoardAgentService) that Anvilboard.Api
// exposes over HTTP, so a coding agent can list/create/update issues and read the dashboard either
// as one-shot shell commands or as an MCP server a host application talks to over stdio - without a
// bespoke client for either surface.
//
// Both modes share one DI container/catalog/invoker construction. They are never active at once in
// a single process invocation: MCP's stdio transport reserves stdout exclusively for JSON-RPC (see
// McpOperationServer.RunStdioAsync), so the mode is chosen up front from args[0], mirroring
// dotnet-agent-surface's own Samples.CliAndMcp host.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("ANVILBOARD_")
    .Build();

var services = new ServiceCollection();

// Logs always go to stderr, never stdout: in MCP mode stdout is reserved exclusively for JSON-RPC
// protocol traffic, and in CLI mode it keeps command output (stdout) free of incidental log noise
// so it stays script/pipe friendly.
services.AddLogging(builder =>
{
    builder.AddConfiguration(configuration.GetSection("Logging"));
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
});
services.AddAnvilboardInfrastructure(configuration);
services.AddAnvilboardApplication();
services.AddGitHubIntegration(configuration);
services.AddLinearIntegration(configuration);
services.AddScoped<BoardAgentService>();

// The credential is a process-wide setting, but the actor it resolves to is per-invocation: the
// accessor and the idempotency helper are scoped so one invocation can never observe another's
// actor or replay state.
var agentOptions = configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
services.AddSingleton(agentOptions);
services.AddSingleton<AgentCredentialSource>();
services.AddScoped<AgentActorAccessor>();
services.AddScoped<AgentWorkspaceScope>();
services.AddScoped<AgentIdempotency>();

var isMcpMode = args is ["mcp", ..];
if (isMcpMode)
{
    // The MCP server is a long-running process (a host application keeps it alive over stdio for
    // the whole agent session), so - unlike a one-shot CLI invocation - it's also worth running the
    // background sync loop that polls GitHub/Linear, matching the API host's behavior.
    services.AddAnvilboardSyncCoordinator();
}

await using var provider = services.BuildServiceProvider();

// Applying migrations here (rather than requiring the Api host to have been run first) lets the
// agent surface work standalone against a fresh SQLite file - useful for agent-only deployments or
// automated/headless environments with no API host running.
using (var scope = provider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
    await db.Database.MigrateAsync();
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
jsonOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory());

// Lets CLI/MCP callers pass "InProgress" instead of the numeric 2 for IssueStatus/IssuePriority -
// friendlier for a human typing a command or an agent composing a tool call than the Api host's raw
// numeric wire format (which stays numeric for compactness/back-compat). JsonStringEnumConverter
// still accepts numbers too, so numeric input keeps working.
jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

// WorkspaceAuthorizationPolicy is registered as a global invocation policy rather than being called
// from inside each operation, so it runs before argument binding and before the operation target is
// resolved: a denied invocation constructs no application service and opens no transaction. It is
// also the only way to make "every operation is authorized" structurally true instead of a
// convention each new operation could forget.
var invoker = new OperationInvoker(
    new AgentInvocationServiceProvider(),
    jsonOptions,
    [new WorkspaceAuthorizationPolicy()]);

if (isMcpMode)
{
    var hostedServices = provider.GetServices<IHostedService>().ToList();
    foreach (var hosted in hostedServices)
    {
        await hosted.StartAsync(CancellationToken.None);
    }

    var server = new McpOperationServer(new McpOperationAdapter(catalog, invoker));
    var options = server.CreateOptions();

    // Each MCP tool call is an independent invocation and must get its own DI scope, disposed when
    // the call completes. Decorating the handler (rather than letting RunStdioAsync own the loop)
    // is the only seam the package exposes for per-call host state; without it every call in a
    // long-lived session would share one container and leak a DbContext per call.
    var innerCallTool = options.Handlers.CallToolHandler!;
    options.Handlers.CallToolHandler = async (context, cancellationToken) =>
    {
        using var invocationScope = AgentInvocationScope.Begin(provider);
        return await innerCallTool(context, cancellationToken);
    };

    var transport = new StdioServerTransport(options);
    await McpServer.Create(transport, options).RunAsync();

    foreach (var hosted in hostedServices)
    {
        await hosted.StopAsync(CancellationToken.None);
    }

    return 0;
}

var adapter = new OperationCommandLineAdapter(catalog, invoker, new JsonAgentOutputRenderer());

// A CLI process performs exactly one invocation, so the whole command runs in a single scope that
// is disposed before the process exits.
CommandLineExecutionResult result;
using (AgentInvocationScope.Begin(provider))
{
    result = await adapter.ExecuteAsync(args);
}

if (!string.IsNullOrEmpty(result.Output))
{
    Console.WriteLine(result.Output);
}

if (!string.IsNullOrEmpty(result.Error))
{
    Console.Error.WriteLine(result.Error);
}

return result.ExitCode;
