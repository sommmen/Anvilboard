using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the whole application, backed by one local SQLite file
/// (see <see cref="AnvilboardDbOptions.DatabasePath"/>). One file, no external database server,
/// is central to the "low-resource, run in-process" goal: the entire board's state is one file
/// that can be backed up by copying it.
/// </summary>
public sealed class AnvilboardDbContext(DbContextOptions<AnvilboardDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<ExternalLink> ExternalLinks => Set<ExternalLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnvilboardDbContext).Assembly);
    }
}
