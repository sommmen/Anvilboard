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
        // No HasDefaultValue(Role.Contributor) here: Role.Administrator is the enum's ordinal 0
        // (the CLR default), and EF Core omits a database-defaulted column from the INSERT
        // whenever the in-memory value equals the CLR default - silently downgrading every
        // explicitly-assigned Administrator to the configured default (Contributor). The C#
        // property initializer on Member.Role already supplies Contributor for callers that
        // don't set a role explicitly, so no DB-level default is needed.
        builder.Property(m => m.Username).HasMaxLength(200);
        builder.HasIndex(m => new { m.WorkspaceId, m.Email });
        builder.HasIndex(m => new { m.WorkspaceId, m.Username }).IsUnique();
    }
}
