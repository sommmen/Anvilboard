using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasConversion<StronglyTypedIdValueConverter<LabelId>>();
        builder.Property(l => l.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(l => l.Name).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Color).HasMaxLength(7).IsRequired();
        builder.HasIndex(l => new { l.WorkspaceId, l.Name }).IsUnique();
    }
}
