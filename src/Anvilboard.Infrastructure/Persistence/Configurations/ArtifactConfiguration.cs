using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.HasKey(artifact => artifact.Id);
        builder.Property(artifact => artifact.Id).HasConversion<StronglyTypedIdValueConverter<ArtifactId>>();
        builder.Property(artifact => artifact.IssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(artifact => artifact.AddedById).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();

        builder.Property(artifact => artifact.Kind)
            .HasConversion(
                kind => ArtifactKindConverter.ToWireValue(kind),
                value => ArtifactKindConverter.Parse(value))
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(artifact => artifact.Title).HasMaxLength(500).IsRequired();
        builder.Property(artifact => artifact.ContentReference).IsRequired();
        builder.Property(artifact => artifact.Source).HasMaxLength(100).IsRequired();
        builder.Property(artifact => artifact.DedupKey).HasMaxLength(500);

        builder.HasIndex(artifact => artifact.IssueId);
        builder.HasIndex(artifact => new { artifact.IssueId, artifact.DedupKey }).IsUnique()
            .HasFilter("\"DedupKey\" IS NOT NULL");
    }
}
