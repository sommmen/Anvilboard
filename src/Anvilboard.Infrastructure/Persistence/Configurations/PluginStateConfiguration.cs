using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class PluginStateConfiguration : IEntityTypeConfiguration<PluginState>
{
    public void Configure(EntityTypeBuilder<PluginState> builder)
    {
        builder.HasKey(state => new { state.WorkspaceId, state.PluginKey, state.StateKey });
        builder.Property(state => state.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(state => state.PluginKey).HasMaxLength(100).IsRequired();
        builder.Property(state => state.StateKey).HasMaxLength(200).IsRequired();
        builder.Property(state => state.Value).IsRequired();
        builder.HasIndex(state => state.WorkspaceId);
    }
}
