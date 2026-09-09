using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasConversion<StronglyTypedIdValueConverter<AuditEventId>>();
        builder.Property(a => a.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(a => a.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Channel)
            .HasConversion(
                channel => channel.ToString().ToUpperInvariant(),
                value => Enum.Parse<AuditChannel>(value, ignoreCase: true))
            .HasMaxLength(10)
            .IsRequired();
        builder.Property(a => a.Action).HasMaxLength(200).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.TargetId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.ResultSummary).IsRequired();
        builder.HasIndex(a => new { a.WorkspaceId, a.OccurredAt });
    }
}
