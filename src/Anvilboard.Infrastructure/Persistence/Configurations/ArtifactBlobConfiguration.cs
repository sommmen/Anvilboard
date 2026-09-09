using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class ArtifactBlobConfiguration : IEntityTypeConfiguration<ArtifactBlob>
{
    public void Configure(EntityTypeBuilder<ArtifactBlob> builder)
    {
        builder.HasKey(blob => blob.Reference);
        builder.Property(blob => blob.Reference).HasMaxLength(200);
        builder.Property(blob => blob.ContentType).HasMaxLength(200);
    }
}
