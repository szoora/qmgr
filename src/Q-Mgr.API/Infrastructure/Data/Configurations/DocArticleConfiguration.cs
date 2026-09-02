using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Docs;

namespace QMgr.Infrastructure.Data.Configurations;

public class DocArticleConfiguration : IEntityTypeConfiguration<DocArticle>
{
    public void Configure(EntityTypeBuilder<DocArticle> builder)
    {
        builder.ToTable("doc_articles");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Title).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Slug).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(500);
        builder.Property(a => a.BodyHtml).IsRequired();
        builder.Property(a => a.CoverImageUrl).HasMaxLength(1000);

        builder.HasIndex(a => a.Slug).IsUnique().HasDatabaseName("idx_doc_articles_slug");
        builder.HasIndex(a => new { a.Status, a.DisplayOrder }).HasDatabaseName("idx_doc_articles_status_order");
    }
}
