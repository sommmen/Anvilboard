using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasConversion<StronglyTypedIdValueConverter<IdempotencyRecordId>>();
        builder.Property(r => r.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(r => r.ActorId).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Operation).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Key).HasMaxLength(255).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(200).IsRequired();
        builder.Property(r => r.ResultPayload).IsRequired();

        // Composite lookup key per tech-design §10.3 Index Strategy: a committed idempotency key
        // is scoped to one workspace/actor/operation and can only ever resolve to one record.
        builder.HasIndex(r => new { r.WorkspaceId, r.ActorId, r.Operation, r.Key }).IsUnique();
    }
}
