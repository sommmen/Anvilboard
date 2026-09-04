using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Anvilboard.Infrastructure.Persistence.Configurations;

public sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasConversion<StronglyTypedIdValueConverter<MemberId>>();
        builder.Property(m => m.WorkspaceId).HasConversion<StronglyTypedIdValueConverter<WorkspaceId>>();
        builder.Property(m => m.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Email).HasMaxLength(320);
        builder.HasIndex(m => new { m.WorkspaceId, m.Email });
    }
}
