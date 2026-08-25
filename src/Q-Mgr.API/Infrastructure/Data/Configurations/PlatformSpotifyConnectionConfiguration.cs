using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Platform;

namespace QMgr.Infrastructure.Data.Configurations;

public class PlatformSpotifyConnectionConfiguration : IEntityTypeConfiguration<PlatformSpotifyConnection>
{
    public void Configure(EntityTypeBuilder<PlatformSpotifyConnection> builder)
    {
        builder.ToTable("platform_spotify_connections");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.SpotifyUserId).HasMaxLength(255).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(255);
        builder.Property(c => c.AccessTokenProtected).IsRequired();
        builder.Property(c => c.RefreshTokenProtected).IsRequired();
        builder.Property(c => c.Scopes).HasMaxLength(500).IsRequired();
    }
}
