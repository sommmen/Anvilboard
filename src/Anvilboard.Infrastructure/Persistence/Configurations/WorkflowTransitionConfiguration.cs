using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class WorkflowTransitionConfiguration : IEntityTypeConfiguration<WorkflowTransition>
{
    public void Configure(EntityTypeBuilder<WorkflowTransition> builder)
    {
        builder.HasKey(transition => transition.Id);
        builder.Property(transition => transition.Id)
            .HasConversion<StronglyTypedIdValueConverter<WorkflowTransitionId>>();
        builder.Property(transition => transition.WorkspaceId)
            .HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(transition => transition.FromStateId)
            .HasConversion<StronglyTypedIdValueConverter<WorkflowStateId>>();
        builder.Property(transition => transition.ToStateId)
            .HasConversion<StronglyTypedIdValueConverter<WorkflowStateId>>();
        builder.HasIndex(transition => new
        {
            transition.WorkspaceId,
            transition.FromStateId,
            transition.ToStateId,
        }).IsUnique();
    }
}
