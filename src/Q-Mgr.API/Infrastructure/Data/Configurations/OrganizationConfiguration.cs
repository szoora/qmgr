using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Organization;

namespace QMgr.Infrastructure.Data.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(o => o.BrandName)
            .HasMaxLength(100);

        builder.Property(o => o.LogoUrl)
            .HasMaxLength(500);

        builder.Property(o => o.ContactEmail)
            .HasMaxLength(255);

        builder.Property(o => o.ContactPhone)
            .HasMaxLength(50);

        builder.Property(o => o.Website)
            .HasMaxLength(255);

        builder.Property(o => o.Settings)
            .HasColumnType("jsonb");

        builder.HasIndex(o => o.Name)
            .IsUnique()
            .HasDatabaseName("idx_organizations_name");
    }
}

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(b => b.Code)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.Timezone)
            .HasMaxLength(50)
            .HasDefaultValue("UTC");

        builder.Property(b => b.OperatingHours)
            .HasColumnType("jsonb");

        builder.Property(b => b.Settings)
            .HasColumnType("jsonb");

        builder.HasIndex(b => b.Code)
            .IsUnique()
            .HasDatabaseName("idx_branches_code");

        builder.HasOne(b => b.Organization)
            .WithMany(o => o.Branches)
            .HasForeignKey(b => b.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class BranchSettingsConfiguration : IEntityTypeConfiguration<BranchSettings>
{
    public void Configure(EntityTypeBuilder<BranchSettings> builder)
    {
        builder.ToTable("branch_settings");

        builder.HasKey(bs => bs.Id);

        builder.Property(bs => bs.DefaultKioskPrinter)
            .HasMaxLength(255);

        builder.Property(bs => bs.VoiceLanguage)
            .HasMaxLength(10);

        builder.Property(bs => bs.SmsTemplateTokenCreated)
            .HasMaxLength(500);

        builder.Property(bs => bs.SmsTemplateTokenCalled)
            .HasMaxLength(500);

        builder.Property(bs => bs.KioskSettingsJson)
            .HasColumnType("jsonb");

        builder.HasOne(bs => bs.Branch)
            .WithOne()
            .HasForeignKey<BranchSettings>(bs => bs.BranchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
