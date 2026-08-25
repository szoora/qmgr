using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Integration;

namespace QMgr.Infrastructure.Data.Configurations;

public class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("api_clients");

        builder.HasKey(ac => ac.Id);

        builder.Property(ac => ac.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(ac => ac.ClientId)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(ac => ac.ClientSecretHash)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(ac => ac.SystemType)
            .HasMaxLength(100);

        builder.Property(ac => ac.Scopes)
            .HasColumnType("text[]");

        builder.Property(ac => ac.AllowedBranches)
            .HasColumnType("uuid[]");

        builder.Property(ac => ac.WebhookUrl)
            .HasMaxLength(500);

        builder.Property(ac => ac.WebhookEvents)
            .HasColumnType("text[]");

        builder.Property(ac => ac.WebhookSecret)
            .HasMaxLength(255);

        builder.HasIndex(ac => ac.ClientId)
            .IsUnique()
            .HasDatabaseName("idx_api_clients_client_id");

        builder.HasOne(ac => ac.Organization)
            .WithMany()
            .HasForeignKey(ac => ac.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApiLogConfiguration : IEntityTypeConfiguration<ApiLog>
{
    public void Configure(EntityTypeBuilder<ApiLog> builder)
    {
        builder.ToTable("api_logs");

        builder.HasKey(al => al.Id);

        builder.Property(al => al.Endpoint)
            .HasMaxLength(255);

        builder.Property(al => al.Method)
            .HasMaxLength(10);

        builder.Property(al => al.RequestBody)
            .HasColumnType("jsonb");

        builder.Property(al => al.IpAddress)
            .HasMaxLength(45);

        builder.HasIndex(al => new { al.ApiClientId, al.CreatedAt })
            .HasDatabaseName("idx_api_logs_client_date");

        builder.HasOne(al => al.ApiClient)
            .WithMany(ac => ac.ApiLogs)
            .HasForeignKey(al => al.ApiClientId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class WebhookOutgoingConfiguration : IEntityTypeConfiguration<WebhookOutgoing>
{
    public void Configure(EntityTypeBuilder<WebhookOutgoing> builder)
    {
        builder.ToTable("webhooks_outgoing");

        builder.HasKey(wo => wo.Id);

        builder.Property(wo => wo.EventType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(wo => wo.Payload)
            .HasColumnType("jsonb");

        builder.Property(wo => wo.Status)
            .HasMaxLength(20)
            .HasDefaultValue("pending");

        builder.HasIndex(wo => new { wo.Status, wo.CreatedAt })
            .HasDatabaseName("idx_webhooks_status_date");

        builder.HasOne(wo => wo.ApiClient)
            .WithMany(ac => ac.WebhooksOutgoing)
            .HasForeignKey(wo => wo.ApiClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
