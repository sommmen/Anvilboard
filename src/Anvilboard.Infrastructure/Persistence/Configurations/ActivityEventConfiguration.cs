using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasConversion<StronglyTypedIdValueConverter<ActivityEventId>>();
        builder.Property(a => a.IssueId).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(a => a.ActorId).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();
        builder.HasIndex(a => a.IssueId);
        builder.HasIndex(a => a.OccurredAt);
    }
}
