using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Organization;

namespace QMgr.Infrastructure.Data.Configurations;

/// <summary>
/// Indexes behind duplicate-registration detection. The unique one on the canonical email is the
/// part that actually enforces anything; the rest exist so a lookup during sign-up stays cheap.
/// </summary>
public class UserIdentityConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(u => u.NormalizedPhone).HasMaxLength(32);

        // The real guard. An application-level check alone loses to two simultaneous sign-ups, and
        // the previous exact-match index on Email could not see that a dotted or plus-tagged Gmail
        // address is the same mailbox.
        builder.HasIndex(u => u.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("idx_users_normalized_email_unique");

        // Not unique on purpose: an unverified number can legitimately collide (a shared office
        // line, a typo), so it feeds the risk score. Only a *verified* number blocks, and that is
        // enforced in RegistrationGuardService where the verification state can be checked too.
        builder.HasIndex(u => u.NormalizedPhone)
            .HasDatabaseName("idx_users_normalized_phone");
    }
}

/// <summary>
/// Name-matching indexes. Neither is unique: two unrelated businesses can share a name, and a
/// second branch of one business is a legitimate second sign-up. These narrow the candidate set so
/// similarity scoring runs in process over a handful of rows.
/// </summary>
public class OrganizationIdentityConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.Property(o => o.NormalizedName).HasMaxLength(200);
        builder.Property(o => o.NameBlockingKey).HasMaxLength(32);

        builder.HasIndex(o => o.NormalizedName)
            .HasDatabaseName("idx_organizations_normalized_name");

        // The one the sign-up path actually queries.
        builder.HasIndex(o => o.NameBlockingKey)
            .HasDatabaseName("idx_organizations_name_blocking_key");
    }
}

public class RegistrationAttemptConfiguration : IEntityTypeConfiguration<RegistrationAttempt>
{
    public void Configure(EntityTypeBuilder<RegistrationAttempt> builder)
    {
        builder.ToTable("registration_attempts");

        builder.Property(a => a.Email).HasMaxLength(256).IsRequired();
        builder.Property(a => a.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(a => a.Phone).HasMaxLength(32);
        builder.Property(a => a.NormalizedPhone).HasMaxLength(32);
        builder.Property(a => a.OrganizationName).HasMaxLength(200).IsRequired();
        builder.Property(a => a.NormalizedName).HasMaxLength(200);
        builder.Property(a => a.NameBlockingKey).HasMaxLength(32);
        builder.Property(a => a.ContactFirstName).HasMaxLength(100);
        builder.Property(a => a.ContactLastName).HasMaxLength(100);
        builder.Property(a => a.ClientAddressHash).HasMaxLength(64);
        builder.Property(a => a.ReviewNotes).HasMaxLength(1000);

        // Drives the "same network recently" signal, which is time-bounded.
        builder.HasIndex(a => new { a.ClientAddressHash, a.CreatedAt })
            .HasDatabaseName("idx_registration_attempts_address_time");

        // Drives the review queue, which lists undecided flags newest first.
        builder.HasIndex(a => new { a.Decision, a.ReviewedAt })
            .HasDatabaseName("idx_registration_attempts_decision_reviewed");

        builder.HasIndex(a => a.NormalizedEmail)
            .HasDatabaseName("idx_registration_attempts_normalized_email");
    }
}
