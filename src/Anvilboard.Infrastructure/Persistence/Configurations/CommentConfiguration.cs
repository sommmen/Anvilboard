using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasConversion<StronglyTypedIdValueConverter<CommentId>>();
        builder.Property(c => c.IssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(c => c.AuthorId).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();
        builder.Property(c => c.Body).IsRequired();
        builder.HasIndex(c => c.IssueId);
    }
}
