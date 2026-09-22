using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Identity;

namespace QMgr.Infrastructure.Data.Configurations;

public class UserDeviceSessionConfiguration : IEntityTypeConfiguration<UserDeviceSession>
{
    public void Configure(EntityTypeBuilder<UserDeviceSession> builder)
    {
        builder.HasKey(s => s.Id);

        // Computed conveniences over the columns beside them. Mapping either would let the two
        // disagree — the same reasoning as ClassTeacherAssignment.IsLive.
        builder.Ignore(s => s.IsLive);
        builder.Ignore(s => s.CanReceivePush);

        builder.Property(s => s.DeviceId).HasMaxLength(64).IsRequired();
        builder.Property(s => s.DeviceName).HasMaxLength(120);
        builder.Property(s => s.Platform).HasMaxLength(20);
        builder.Property(s => s.AppVersion).HasMaxLength(40);
        builder.Property(s => s.CredentialStamp).HasMaxLength(64);
        builder.Property(s => s.RevokedReason).HasMaxLength(200);

        // Base64 SHA-256 is 44 characters; the column is sized for the value rather than left as
        // unbounded text, so a row that is not a hash is visibly wrong.
        builder.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();

        // An FCM registration token has no documented maximum and has grown over the years, so
        // this is generous rather than exact. It is still bounded: an unbounded column here is
        // somewhere a client can write as much as it likes.
        builder.Property(s => s.PushToken).HasMaxLength(4000);

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // ONE session row per (user, device). This is the index the redeem path uses, and it is
        // UNIQUE on purpose: re-signing in on a handset must replace that handset's token rather
        // than leave two rows where only one of them can ever be redeemed.
        builder.HasIndex(s => new { s.UserId, s.DeviceId })
            .IsUnique()
            .HasDatabaseName("idx_device_session_user_device");

        // "Which handsets can I notify?" — run per push fan-out, so it must be a seek. Partial,
        // because a revoked session is never a target and most rows eventually are.
        builder.HasIndex(s => s.UserId)
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("idx_device_session_user_live");

        // The organisation is denormalised from the user so the tenant purge can reach these rows
        // in one hop; this index is what makes that delete a seek rather than a table scan.
        builder.HasIndex(s => s.OrganizationId)
            .HasDatabaseName("idx_device_session_org");
    }
}
