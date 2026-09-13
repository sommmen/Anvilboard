using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class IntegrationHealthConfiguration : IEntityTypeConfiguration<IntegrationHealth>
{
    public void Configure(EntityTypeBuilder<IntegrationHealth> builder)
    {
        builder.HasKey(health => health.Id);
        builder.Property(health => health.Id).HasConversion<StronglyTypedIdValueConverter<IntegrationHealthId>>();
        builder.Property(health => health.IntegrationId).HasConversion<StronglyTypedIdValueConverter<IntegrationId>>();
        builder.Property(health => health.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(health => health.PluginKey).IsRequired().HasMaxLength(100);
        builder.Property(health => health.LastErrorCategory).HasConversion<int?>();
        builder.Property(health => health.LastCursorToken).IsRequired(false);

        // One health row per integration: the unique index is what makes the coordinator's
        // read-modify-write upsert safe when two loops race for the same target.
        builder.HasIndex(health => health.IntegrationId).IsUnique();
        builder.HasIndex(health => health.WorkspaceId);

        // A removed integration must not leave an orphan health row behind that would keep
        // reporting a condition for something that no longer exists.
        builder.HasOne<Integration>()
            .WithMany()
            .HasForeignKey(health => health.IntegrationId)
            .HasPrincipalKey(integration => integration.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
