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
        // that number has to BE a key rather than a label. Partial, because most of the platform's
        // users (a bank's counter staff, a tenant administrator) carry no employee number at all and
        // any number of those nulls must be able to coexist.
        builder.HasIndex(u => new { u.OrganizationId, u.EmployeeNumber })
            .IsUnique()
            .HasFilter("\"EmployeeNumber\" IS NOT NULL")
            .HasDatabaseName("idx_users_employee_number_unique");

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
