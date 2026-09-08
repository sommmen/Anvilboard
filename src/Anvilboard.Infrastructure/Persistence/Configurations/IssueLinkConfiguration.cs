using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class IssueLinkConfiguration : IEntityTypeConfiguration<IssueLink>
{
    public void Configure(EntityTypeBuilder<IssueLink> builder)
    {
        builder.HasKey(link => link.Id);
        builder.Property(link => link.Id).HasConversion<StronglyTypedIdValueConverter<IssueLinkId>>();
        builder.Property(link => link.SourceIssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(link => link.TargetIssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(link => link.CreatedById).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();
        builder.Property(link => link.Type).HasMaxLength(100).IsRequired();
        builder.Property(link => link.Description).IsRequired();
        builder.HasIndex(link => new { link.SourceIssueId, link.TargetIssueId, link.Type }).IsUnique();
        builder.HasIndex(link => link.SourceIssueId);
        builder.HasIndex(link => link.TargetIssueId);
    }
}
