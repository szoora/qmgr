using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Infrastructure.Data.Configurations;

/// <summary>
/// NOTE FOR THE MIGRATION/DBCONTEXT OWNER: this configuration is what puts FeedbackQuestion into
/// the model (QMgrDbContext calls ApplyConfigurationsFromAssembly, and ModelBuilder.Entity&lt;T&gt;()
/// registers the type), so the feature works without a DbSet property — FeedbackController uses
/// _context.Set&lt;FeedbackQuestion&gt;(). Adding
///     public DbSet&lt;FeedbackQuestion&gt; FeedbackQuestions => Set&lt;FeedbackQuestion&gt;();
/// to QMgrDbContext is nice-to-have for readability but is not required.
/// </summary>
public class FeedbackQuestionConfiguration : IEntityTypeConfiguration<FeedbackQuestion>
{
    public void Configure(EntityTypeBuilder<FeedbackQuestion> builder)
    {
        builder.ToTable("feedback_questions");

        builder.HasKey(q => q.Id);

        builder.Property(q => q.QuestionText)
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(q => q.OptionsJson)
            .HasMaxLength(2000);

        builder.Property(q => q.QuestionType)
            .IsRequired();

        builder.Property(q => q.DisplayOrder)
            .IsRequired();

        // Rendering order for the public form: branch + active + order is the exact predicate
        // the anonymous read uses on every public feedback page load.
        builder.HasIndex(q => new { q.BranchId, q.IsActive, q.DisplayOrder })
            .HasDatabaseName("idx_feedback_question_branch_order");

        // Org-wide questions (BranchId == null) are fetched by organization.
        builder.HasIndex(q => new { q.OrganizationId, q.IsActive })
            .HasDatabaseName("idx_feedback_question_org");

        builder.HasOne(q => q.Organization)
            .WithMany()
            .HasForeignKey(q => q.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(q => q.Branch)
            .WithMany()
            .HasForeignKey(q => q.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(q => q.ServiceType)
            .WithMany()
            .HasForeignKey(q => q.ServiceTypeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
