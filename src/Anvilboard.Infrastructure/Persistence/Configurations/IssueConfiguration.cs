using System.Text.Json;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasConversion<StronglyTypedIdValueConverter<IssueId>>();
        builder.Property(i => i.TeamId).HasConversion<StronglyTypedIdValueConverter<TeamId>>();
        builder.Property(i => i.ProjectId).HasConversion<StronglyTypedIdValueConverter<ProjectId>?>();
        builder.Property(i => i.AssigneeId).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();
        builder.Property(i => i.CreatedById).HasConversion<StronglyTypedIdValueConverter<MemberId>?>();
        builder.Property(i => i.Key).HasMaxLength(20).IsRequired();
        builder.Property(i => i.Title).HasMaxLength(500).IsRequired();

        // LabelIds is a small, order-insignificant set with no independent query needs of its own
        // (label filtering happens by loading Labels and intersecting in memory, which is cheap at
        // the single-team, single-user scale this project targets) so it is stored as a JSON array
        // rather than a normalized join table, trading a join for a simpler schema.
        builder.Property(i => i.LabelIds)
            .HasConversion(
                ids => JsonSerializer.Serialize(ids.Select(id => id.Value), (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null)!
                    .Select(g => new LabelId(g)).ToList(),
                new ValueComparer<List<LabelId>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.Value)),
                    v => v.ToList()));

        builder.HasIndex(i => new { i.TeamId, i.Key }).IsUnique();
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.ProjectId);
        builder.HasIndex(i => i.AssigneeId);
    }
}
