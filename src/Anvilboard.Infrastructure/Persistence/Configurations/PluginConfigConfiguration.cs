using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class PluginConfigConfiguration : IEntityTypeConfiguration<PluginConfig>
{
    public void Configure(EntityTypeBuilder<PluginConfig> builder)
    {
        builder.HasKey(config => new { config.WorkspaceId, config.PluginKey, config.ConfigKey });
        builder.Property(config => config.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(config => config.PluginKey).HasMaxLength(100).IsRequired();
        builder.Property(config => config.ConfigKey).HasMaxLength(200).IsRequired();
        builder.Property(config => config.Value).IsRequired();
        builder.Property(config => config.IsSecret).HasDefaultValue(false);
        builder.HasIndex(config => config.WorkspaceId);
    }
}
