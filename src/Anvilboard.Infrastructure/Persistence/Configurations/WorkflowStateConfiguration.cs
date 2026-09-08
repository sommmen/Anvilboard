using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class WorkflowStateConfiguration : IEntityTypeConfiguration<WorkflowState>
{
    public void Configure(EntityTypeBuilder<WorkflowState> builder)
    {
        builder.HasKey(state => state.Id);
        builder.Property(state => state.Id).HasConversion<StronglyTypedIdValueConverter<WorkflowStateId>>();
        builder.Property(state => state.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(state => state.Key).HasMaxLength(100).IsRequired();
        builder.Property(state => state.DisplayName).HasMaxLength(200).IsRequired();
        builder.HasIndex(state => new { state.WorkspaceId, state.Key }).IsUnique();
    }
}
