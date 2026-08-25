using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Infrastructure.Data.Configurations;

public class ServiceTypeConfiguration : IEntityTypeConfiguration<ServiceType>
{
    public void Configure(EntityTypeBuilder<ServiceType> builder)
    {
        builder.ToTable("service_types");

        builder.HasKey(st => st.Id);

        builder.Property(st => st.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(st => st.Code)
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(st => st.Prefix)
            .HasMaxLength(5);

        builder.Property(st => st.IconUrl)
            .HasMaxLength(500);

        builder.Property(st => st.Color)
            .HasMaxLength(10);

        builder.HasIndex(st => new { st.BranchId, st.Code })
            .IsUnique()
            .HasDatabaseName("idx_service_types_branch_code");

        builder.HasOne(st => st.Branch)
            .WithMany(b => b.ServiceTypes)
            .HasForeignKey(st => st.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class CounterConfiguration : IEntityTypeConfiguration<Counter>
{
    public void Configure(EntityTypeBuilder<Counter> builder)
    {
        builder.ToTable("counters");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CounterNumber)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.DisplayName)
            .HasMaxLength(100);

        builder.HasIndex(c => new { c.BranchId, c.CounterNumber })
            .IsUnique()
            .HasDatabaseName("idx_counters_branch_number");

        builder.HasOne(c => c.Branch)
            .WithMany(b => b.Counters)
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.CurrentToken)
            .WithOne()
            .HasForeignKey<Counter>(c => c.CurrentTokenId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(c => c.AssignedUser)
            .WithOne(u => u.AssignedCounter)
            .HasForeignKey<Counter>(c => c.AssignedUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class CounterServiceTypeConfiguration : IEntityTypeConfiguration<CounterServiceType>
{
    public void Configure(EntityTypeBuilder<CounterServiceType> builder)
    {
        builder.ToTable("counter_service_types");

        builder.HasKey(cst => cst.Id);

        builder.HasIndex(cst => new { cst.CounterId, cst.ServiceTypeId })
            .IsUnique()
            .HasDatabaseName("idx_counter_service_types_unique");

        builder.HasOne(cst => cst.Counter)
            .WithMany(c => c.CounterServiceTypes)
            .HasForeignKey(cst => cst.CounterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(cst => cst.ServiceType)
            .WithMany(st => st.CounterServiceTypes)
            .HasForeignKey(cst => cst.ServiceTypeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
