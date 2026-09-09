using System.Text.Json;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasConversion<StronglyTypedIdValueConverter<ApiTokenId>>();
        builder.Property(t => t.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(t => t.MemberId).HasConversion<StronglyTypedIdValueConverter<MemberId>>();
        builder.Property(t => t.TokenHash).HasMaxLength(200).IsRequired();

        // GrantedPermissions is a small, fixed-at-issuance set with no independent query needs of
        // its own, so it is stored as a JSON array rather than a normalized join table (same
        // trade-off Issue.LabelIds makes).
        builder.Property(t => t.GrantedPermissions)
            .HasConversion(
                permissions => JsonSerializer.Serialize(permissions, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Permission>>(json, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyList<Permission>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, p) => HashCode.Combine(hash, p)),
                    v => v.ToList()));

        // A revoked/expired token must never authenticate again, so lookups are always by hash.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.WorkspaceId);
        builder.HasIndex(t => t.MemberId);
    }
}
