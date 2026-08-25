using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Infrastructure.Data.Configurations;

public class TokenConfiguration : IEntityTypeConfiguration<Token>
{
    public void Configure(EntityTypeBuilder<Token> builder)
    {
        builder.ToTable("tokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenNumber)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(t => t.DisplayNumber)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(t => t.CustomerId)
            .HasMaxLength(100);

        builder.Property(t => t.CustomerName)
            .HasMaxLength(255);

        builder.Property(t => t.CustomerPhone)
            .HasMaxLength(50);

        builder.Property(t => t.CustomerEmail)
            .HasMaxLength(255);

        builder.Property(t => t.ExternalReference)
            .HasMaxLength(100);

        builder.Property(t => t.ExternalSystem)
            .HasMaxLength(100);

        builder.Property(t => t.Metadata)
            .HasColumnType("jsonb");

        // Indexes
        builder.HasIndex(t => new { t.BranchId, t.CreatedAt })
            .HasDatabaseName("idx_tokens_branch_date");

        builder.HasIndex(t => new { t.BranchId, t.Status })
            .HasDatabaseName("idx_tokens_status");

        builder.HasIndex(t => t.CustomerId)
            .HasDatabaseName("idx_tokens_customer");

        builder.HasIndex(t => new { t.ExternalSystem, t.ExternalReference })
            .HasDatabaseName("idx_tokens_external");

        builder.HasIndex(t => t.DisplayNumber)
            .HasDatabaseName("idx_tokens_display_number");

        // Relationships
        builder.HasOne(t => t.Branch)
            .WithMany(b => b.Tokens)
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.ServiceType)
            .WithMany(st => st.Tokens)
            .HasForeignKey(t => t.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Counter)
            .WithMany(c => c.Tokens)
            .HasForeignKey(t => t.CounterId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class TokenHistoryConfiguration : IEntityTypeConfiguration<TokenHistory>
{
    public void Configure(EntityTypeBuilder<TokenHistory> builder)
    {
        builder.ToTable("token_history");

        builder.HasKey(th => th.Id);

        builder.Property(th => th.Notes)
            .HasMaxLength(500);

        builder.HasOne(th => th.Token)
            .WithMany(t => t.History)
            .HasForeignKey(th => th.TokenId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(th => th.TokenId)
            .HasDatabaseName("idx_token_history_token");
    }
}
