using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Marketing;

namespace QMgr.Infrastructure.Data.Configurations;

public class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.FullName).HasMaxLength(255).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(50);
        builder.Property(c => c.Email).HasMaxLength(255);
        builder.Property(c => c.Tags).HasMaxLength(500);

        builder.HasIndex(c => new { c.OrganizationId, c.OptedOut })
            .HasDatabaseName("idx_contacts_org_optedout");

        // Public opt-out links are looked up by this token alone — must be unique.
        builder.HasIndex(c => c.OptOutToken)
            .IsUnique()
            .HasDatabaseName("idx_contacts_optout_token");

        builder.HasOne(c => c.Organization)
            .WithMany()
            .HasForeignKey(c => c.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Branch)
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class BroadcastConfiguration : IEntityTypeConfiguration<Broadcast>
{
    public void Configure(EntityTypeBuilder<Broadcast> builder)
    {
        builder.ToTable("broadcasts");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name).HasMaxLength(255).IsRequired();
        builder.Property(b => b.Subject).HasMaxLength(500);
        builder.Property(b => b.MessageBody).HasMaxLength(10000).IsRequired();
        builder.Property(b => b.AudienceTagFilter).HasMaxLength(500);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.Channel).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(b => new { b.OrganizationId, b.Status })
            .HasDatabaseName("idx_broadcasts_org_status");

        // The send job polls exactly this shape — Scheduled rows whose time has come.
        builder.HasIndex(b => new { b.Status, b.ScheduledAt })
            .HasDatabaseName("idx_broadcasts_status_scheduled");

        builder.HasOne(b => b.Organization)
            .WithMany()
            .HasForeignKey(b => b.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Branch)
            .WithMany()
            .HasForeignKey(b => b.BranchId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class BroadcastRecipientConfiguration : IEntityTypeConfiguration<BroadcastRecipient>
{
    public void Configure(EntityTypeBuilder<BroadcastRecipient> builder)
    {
        builder.ToTable("broadcast_recipients");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.ErrorMessage).HasMaxLength(1000);

        // A contact can only be enrolled once per broadcast — the send job's own idempotency
        // boundary (see BroadcastSendJob for the concurrency reasoning).
        builder.HasIndex(r => new { r.BroadcastId, r.ContactId })
            .IsUnique()
            .HasDatabaseName("idx_broadcast_recipients_unique");

        builder.HasIndex(r => new { r.BroadcastId, r.Status })
            .HasDatabaseName("idx_broadcast_recipients_status");

        builder.HasOne(r => r.Broadcast)
            .WithMany(b => b.Recipients)
            .HasForeignKey(r => r.BroadcastId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Contact)
            .WithMany()
            .HasForeignKey(r => r.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
