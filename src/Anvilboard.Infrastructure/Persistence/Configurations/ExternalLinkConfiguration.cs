using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class ExternalLinkConfiguration : IEntityTypeConfiguration<ExternalLink>
{
    public void Configure(EntityTypeBuilder<ExternalLink> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasConversion<StronglyTypedIdValueConverter<ExternalLinkId>>();
        builder.Property(e => e.IssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(e => e.SourceKey).HasMaxLength(500).IsRequired();

        // The (Provider, SourceKey) uniqueness is exactly the ingestion dedupe key described on
        // NormalizedIssue: two syncs of the same remote item must resolve to the same row.
        builder.HasIndex(e => new { e.Provider, e.SourceKey }).IsUnique();
        builder.HasIndex(e => e.IssueId);
    }
}
