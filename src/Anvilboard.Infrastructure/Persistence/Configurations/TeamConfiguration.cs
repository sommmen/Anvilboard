using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasConversion<StronglyTypedIdValueConverter<TeamId>>();
        builder.Property(t => t.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Key).HasMaxLength(10).IsRequired();
        builder.HasIndex(t => new { t.WorkspaceId, t.Key }).IsUnique();
    }
}
