using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> builder)
    {
        builder.HasKey(integration => integration.Id);
        builder.Property(integration => integration.Id).HasConversion<StronglyTypedIdValueConverter<IntegrationId>>();
        builder.Property(integration => integration.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(integration => integration.Provider).HasConversion<int>();
        builder.Property(integration => integration.Status).HasConversion<int>();
        builder.Property(integration => integration.ProtectedCredentials).IsRequired(false);
        builder.Property(integration => integration.SettingsJson).IsRequired();
        builder.HasIndex(integration => integration.WorkspaceId);
        builder.HasIndex(integration => new { integration.WorkspaceId, integration.Provider });
    }
}
