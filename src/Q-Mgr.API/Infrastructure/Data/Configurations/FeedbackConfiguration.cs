using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Infrastructure.Data.Configurations;

public class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.ToTable("feedbacks");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.FeedbackCode)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(f => f.Rating)
            .IsRequired();

        builder.Property(f => f.Comment)
            .HasMaxLength(2000);

        builder.Property(f => f.CustomerName)
            .HasMaxLength(255);

        builder.Property(f => f.CustomerPhone)
            .HasMaxLength(50);

        builder.Property(f => f.CustomerEmail)
            .HasMaxLength(255);

        builder.Property(f => f.TokenDisplayNumber)
            .HasMaxLength(30);

        builder.Property(f => f.Response)
            .HasMaxLength(2000);

        // Indexes
        builder.HasIndex(f => f.FeedbackCode)
            .IsUnique()
            .HasDatabaseName("idx_feedback_code");

        builder.HasIndex(f => new { f.BranchId, f.CreatedAt })
            .HasDatabaseName("idx_feedback_branch_date");

        builder.HasIndex(f => new { f.BranchId, f.Rating })
            .HasDatabaseName("idx_feedback_branch_rating");

        // CONCURRENCY: enforces "at most one feedback row per token" at the DB level — closes a
        // real race where two near-simultaneous SubmitFeedbackForToken calls for the same token
        // could both pass the app-level "does feedback already exist" check before either
        // inserted (see docs/TASK_TRACKER.md Phase 4 audit). Postgres unique indexes treat NULL
        // as distinct from every other NULL, so this doesn't restrict feedback rows that have no
        // token (TokenId is nullable).
        builder.HasIndex(f => f.TokenId)
            .IsUnique()
            .HasDatabaseName("idx_feedback_token");

        // Relationships
        builder.HasOne(f => f.Branch)
            .WithMany()
            .HasForeignKey(f => f.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.Token)
            .WithMany()
            .HasForeignKey(f => f.TokenId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.ServiceType)
            .WithMany()
            .HasForeignKey(f => f.ServiceTypeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.Counter)
            .WithMany()
            .HasForeignKey(f => f.CounterId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
