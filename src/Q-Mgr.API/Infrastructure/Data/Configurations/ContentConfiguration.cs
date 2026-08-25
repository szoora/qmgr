using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Content;

namespace QMgr.Infrastructure.Data.Configurations;

public class MediaContentConfiguration : IEntityTypeConfiguration<MediaContent>
{
    public void Configure(EntityTypeBuilder<MediaContent> builder)
    {
        builder.ToTable("media_content");

        builder.HasKey(mc => mc.Id);

        builder.Property(mc => mc.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(mc => mc.MimeType)
            .HasMaxLength(100);

        builder.Property(mc => mc.FilePath)
            .HasMaxLength(500);

        builder.Property(mc => mc.FileUrl)
            .HasMaxLength(500);

        builder.Property(mc => mc.ThumbnailUrl)
            .HasMaxLength(500);

        builder.Property(mc => mc.Dimensions)
            .HasColumnType("jsonb");

        builder.Property(mc => mc.Tags)
            .HasColumnType("text[]");

        builder.HasIndex(mc => new { mc.OrganizationId, mc.ContentType })
            .HasDatabaseName("idx_media_content_org_type");

        builder.HasOne(mc => mc.Organization)
            .WithMany()
            .HasForeignKey(mc => mc.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PlaylistConfiguration : IEntityTypeConfiguration<Playlist>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        builder.ToTable("playlists");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(p => p.ScheduleType)
            .HasMaxLength(20)
            .HasDefaultValue("always");

        builder.Property(p => p.SpotifyPlaylistId).HasMaxLength(64);
        builder.Property(p => p.SpotifyPlaylistName).HasMaxLength(255);

        builder.Property(p => p.Schedule)
            .HasColumnType("jsonb");

        builder.Property(p => p.TransitionType)
            .HasMaxLength(50)
            .HasDefaultValue("fade");

        builder.HasOne(p => p.Branch)
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PlaylistItemConfiguration : IEntityTypeConfiguration<PlaylistItem>
{
    public void Configure(EntityTypeBuilder<PlaylistItem> builder)
    {
        builder.ToTable("playlist_items");

        builder.HasKey(pi => pi.Id);

        builder.Property(pi => pi.Conditions)
            .HasColumnType("jsonb");

        builder.HasIndex(pi => new { pi.PlaylistId, pi.Position })
            .IsUnique()
            .HasDatabaseName("idx_playlist_items_position");

        builder.HasOne(pi => pi.Playlist)
            .WithMany(p => p.Items)
            .HasForeignKey(pi => pi.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pi => pi.MediaContent)
            .WithMany(mc => mc.PlaylistItems)
            .HasForeignKey(pi => pi.MediaContentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(pi => pi.Campaign)
            .WithMany(c => c.PlaylistItems)
            .HasForeignKey(pi => pi.CampaignId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("campaigns");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.HasOne(c => c.Branch)
            .WithMany()
            .HasForeignKey(c => c.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.BranchId, c.StartDate, c.EndDate })
            .HasDatabaseName("idx_campaigns_branch_dates");
    }
}

public class CampaignImpressionConfiguration : IEntityTypeConfiguration<CampaignImpression>
{
    public void Configure(EntityTypeBuilder<CampaignImpression> builder)
    {
        builder.ToTable("campaign_impressions");

        builder.HasKey(ci => ci.Id);

        builder.HasOne(ci => ci.Campaign)
            .WithMany()
            .HasForeignKey(ci => ci.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ci => ci.MediaContent)
            .WithMany()
            .HasForeignKey(ci => ci.MediaContentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ci => new { ci.CampaignId, ci.CreatedAt })
            .HasDatabaseName("idx_campaign_impressions_campaign_time");
    }
}

public class DisplayConfiguration : IEntityTypeConfiguration<Display>
{
    public void Configure(EntityTypeBuilder<Display> builder)
    {
        builder.ToTable("displays");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(d => d.DeviceId)
            .HasMaxLength(100);

        builder.Property(d => d.Resolution)
            .HasColumnType("jsonb");

        builder.Property(d => d.Orientation)
            .HasMaxLength(20)
            .HasDefaultValue("landscape");

        builder.Property(d => d.Status)
            .HasMaxLength(20)
            .HasDefaultValue("offline");

        builder.Property(d => d.Settings)
            .HasColumnType("jsonb");

        builder.HasOne(d => d.Branch)
            .WithMany(b => b.Displays)
            .HasForeignKey(d => d.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class DisplayZoneConfiguration : IEntityTypeConfiguration<DisplayZone>
{
    public void Configure(EntityTypeBuilder<DisplayZone> builder)
    {
        builder.ToTable("display_zones");

        builder.HasKey(dz => dz.Id);

        builder.Property(dz => dz.Name)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(dz => dz.Settings)
            .HasColumnType("jsonb");

        builder.HasOne(dz => dz.Display)
            .WithMany(d => d.DisplayZones)
            .HasForeignKey(dz => dz.DisplayId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(dz => dz.Playlist)
            .WithMany(p => p.DisplayZones)
            .HasForeignKey(dz => dz.PlaylistId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        builder.ToTable("quotes");

        builder.HasKey(q => q.Id);

        builder.Property(q => q.Category)
            .HasMaxLength(100)
            .HasDefaultValue("Motivational");

        builder.Property(q => q.Text)
            .IsRequired();

        builder.Property(q => q.Author)
            .HasMaxLength(255);

        builder.HasOne(q => q.Organization)
            .WithMany()
            .HasForeignKey(q => q.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
