using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Plugins;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Infrastructure.Tests.Plugins;

public sealed class PluginConfigStateStoreTests
{
    [Fact]
    public async Task Configurations_AreNamespaceIsolatedAndSecretValuesAreRedacted()
    {
        await using var connection = await OpenDatabaseAsync();
        var workspace = WorkspaceId.New();
        var otherWorkspace = WorkspaceId.New();

        await using (var db = CreateContext(connection))
        {
            IPluginConfigStore store = new PluginConfigStateStore(db);
            await store.SetAsync(workspace, "github", "base-url", "https://github.example");
            await store.SetAsync(workspace, "linear", "base-url", "https://linear.example");
            await store.SetAsync(otherWorkspace, "github", "base-url", "https://other.example");
            await store.SetAsync(workspace, "github", "token", "secret-value", isSecret: true);

            var config = await store.GetAsync(workspace, "github", "base-url");
            var secret = await store.GetAsync(workspace, "github", "token");

            Assert.Equal("https://github.example", config!.Value);
            Assert.Null(secret!.Value);
            Assert.True(secret.IsSecret);
        }

        await using (var db = CreateContext(connection))
        {
            IPluginConfigStore store = new PluginConfigStateStore(db);

            Assert.Equal("https://linear.example", (await store.GetAsync(workspace, "linear", "base-url"))!.Value);
            Assert.Equal("https://other.example", (await store.GetAsync(otherWorkspace, "github", "base-url"))!.Value);
        }
    }

    [Fact]
    public async Task Set_RejectsEmptyAndOverlengthKeys()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = CreateContext(connection);
        var workspace = WorkspaceId.New();
        IPluginConfigStore configStore = new PluginConfigStateStore(db);
        IPluginStateStore stateStore = new PluginConfigStateStore(db);

        await Assert.ThrowsAsync<ArgumentException>(() => configStore.SetAsync(workspace, "", "valid", "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => configStore.SetAsync(workspace, "valid", "", "value"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => configStore.SetAsync(workspace, new string('p', 101), "valid", "value"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => configStore.SetAsync(workspace, "valid", new string('c', 201), "value"));

        await Assert.ThrowsAsync<ArgumentException>(() => stateStore.SetAsync(workspace, "", "valid", "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => stateStore.SetAsync(workspace, "valid", "", "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stateStore.SetAsync(workspace, new string('p', 101), "valid", "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stateStore.SetAsync(workspace, "valid", new string('s', 201), "{}"));
    }

    [Fact]
    public async Task State_UpsertsAndPersistsAcrossDbContexts()
    {
        await using var connection = await OpenDatabaseAsync();
        var workspace = WorkspaceId.New();

        await using (var db = CreateContext(connection))
        {
            IPluginStateStore store = new PluginConfigStateStore(db);
            await store.SetAsync(workspace, "github", "cursor", "{\"lastId\":1}");
            await store.SetAsync(workspace, "github", "cursor", "{\"lastId\":2}");
        }

        await using (var db = CreateContext(connection))
        {
            IPluginStateStore store = new PluginConfigStateStore(db);
            var state = await store.GetAsync(workspace, "github", "cursor");

            Assert.Equal("{\"lastId\":2}", state!.Value);
            Assert.Null(await store.GetAsync(workspace, "linear", "cursor"));
        }
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AnvilboardDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options);
}
