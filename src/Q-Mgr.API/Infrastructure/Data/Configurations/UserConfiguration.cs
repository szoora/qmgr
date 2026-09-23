using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Identity;

namespace QMgr.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Username)
            .HasMaxLength(100)
            .IsRequired();

        // NOT required. Most staff on a school roll have no email address, and NULL is how that is
        // stored — never "", which the unique index below would refuse the second time.
        builder.Property(u => u.Email)
            .HasMaxLength(255);

        builder.Property(u => u.PasswordHash)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(u => u.FirstName)
            .HasMaxLength(100);

        builder.Property(u => u.LastName)
            .HasMaxLength(100);

        builder.Property(u => u.Phone)
            .HasMaxLength(50);

        builder.Property(u => u.EmployeeNumber)
            .HasMaxLength(50);

        builder.Property(u => u.RefreshToken)
            .HasMaxLength(500);

        builder.HasIndex(u => u.Username)
            .IsUnique()
            .HasDatabaseName("idx_users_username");

        builder.HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName("idx_users_email");

        // Kept explicitly. EF drops a convention index the moment another index starts with the same
        // column — and the composite below is PARTIAL ("where EmployeeNumber is not null"), so it
        // cannot serve the tenant-scoped lookups that every single query in this product makes.
        builder.HasIndex(u => u.OrganizationId)
            .HasDatabaseName("IX_users_OrganizationId");

        // A staff member with no email address is identified by the school's own staff number, so
        // that number has to BE a key rather than a label. Partial, because rows predating the
        // requirement carry no number at all and any number of those nulls must coexist — and
        // because a platform user belongs to no organisation and never has one.
        //
        // THE REAL INDEX IS ON upper("EmployeeNumber") AND IS CREATED IN RAW SQL by
        // 20260922_PersonCodesAreMandatoryAndCaseInsensitive: uniqueness has to be case-insensitive
        // or "MH/S/001" and "mh/s/001" are two members of staff, and EF cannot model a functional
        // index. citext would be the obvious answer and is ruled out — no Postgres extensions. This
        // declaration is kept so the model still knows a unique constraint exists on the pair; the
        // migration drops what EF creates and puts the upper() one in its place.
        builder.HasIndex(u => new { u.OrganizationId, u.EmployeeNumber })
            .IsUnique()
            .HasFilter("\"EmployeeNumber\" IS NOT NULL")
            .HasDatabaseName("idx_users_employee_number_unique");

        // The personal calendar feed (2026-09-23): the anonymous feed request finds its person by the secret's hash.
        builder.Property(u => u.CalendarFeedTokenHash).HasMaxLength(64);
        builder.HasIndex(u => u.CalendarFeedTokenHash)
            .IsUnique()
            .HasFilter("\"CalendarFeedTokenHash\" IS NOT NULL")
            .HasDatabaseName("ux_users_calendar_feed_token");

        builder.HasOne(u => u.Organization)
            .WithMany()
            .HasForeignKey(u => u.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(u => u.AssignedBranch)
            .WithMany()
            .HasForeignKey(u => u.AssignedBranchId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");

        builder.HasKey(us => us.Id);

        builder.HasIndex(us => new { us.UserId, us.LoginTime })
            .HasDatabaseName("idx_user_sessions_user_login");

        builder.HasOne(us => us.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(us => us.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(us => us.Counter)
            .WithMany()
            .HasForeignKey(us => us.CounterId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
