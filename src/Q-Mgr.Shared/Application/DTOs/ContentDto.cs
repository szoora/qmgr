using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record MediaContentDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ContentType ContentType { get; init; }
    public string? MimeType { get; init; }
    public string? FileUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public long? FileSizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
    public string[]? Tags { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record CreateMediaContentRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ContentType ContentType { get; init; }
    public string? MimeType { get; init; }
    public StorageType StorageType { get; init; }
    public string? FileUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public long? FileSizeBytes { get; init; }
    public int? DurationSeconds { get; init; }
    public string? TextContent { get; init; }
    public string[]? Tags { get; init; }
}

public record UpdateMediaContentRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string[]? Tags { get; init; }
}

public record PlaylistDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ScheduleType { get; init; } = "always";
    public string TransitionType { get; init; } = "fade";
    public int DefaultDurationSeconds { get; init; }
    public bool Loop { get; init; }
    public bool Shuffle { get; init; }
    public int ItemCount { get; init; }
    public string? SpotifyPlaylistId { get; init; }
    public string? SpotifyPlaylistName { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record PlaylistDetailDto : PlaylistDto
{
    public string? Schedule { get; init; }
    public List<PlaylistItemDto> Items { get; init; } = new();
}

public record PlaylistItemDto
{
    public Guid Id { get; init; }
    public Guid MediaContentId { get; init; }
    public string MediaName { get; init; } = string.Empty;
    public ContentType MediaType { get; init; }
    public string? FileUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public int DurationSeconds { get; init; }
    public int Position { get; init; }
    public Guid? CampaignId { get; init; }
    public bool CampaignActive { get; init; }
}

public record CreatePlaylistRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ScheduleType { get; init; }
    public string? Schedule { get; init; }
    public string? TransitionType { get; init; }
    public int? DefaultDurationSeconds { get; init; }
    public bool? Loop { get; init; }
    public bool? Shuffle { get; init; }
}

public record UpdatePlaylistRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ScheduleType { get; init; }
    public string? Schedule { get; init; }
    public string? TransitionType { get; init; }
    public int? DefaultDurationSeconds { get; init; }
    public bool? Loop { get; init; }
    public bool? Shuffle { get; init; }
}

public record AddPlaylistItemRequest
{
    public Guid MediaContentId { get; init; }
    public int? DurationSeconds { get; init; }
    public int? Position { get; init; }
    public Guid? CampaignId { get; init; }
}

public record DisplayDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DisplayType DisplayType { get; init; }
    public string? DeviceId { get; init; }
    public string? Resolution { get; init; }
    public string Orientation { get; init; } = "landscape";
    public string Status { get; init; } = "offline";
    public DateTime? LastHeartbeat { get; init; }
    public int ZoneCount { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record DisplayDetailDto : DisplayDto
{
    public string? Settings { get; init; }
    public List<DisplayZoneDto> Zones { get; init; } = new();
}

public record DisplayZoneDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public ZoneType ZoneType { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int ZIndex { get; init; }
    public Guid? PlaylistId { get; init; }
    public string? PlaylistName { get; init; }
}

public record CreateDisplayRequest
{
    public string Name { get; init; } = string.Empty;
    public DisplayType DisplayType { get; init; }
    public string? DeviceId { get; init; }
    public string? Resolution { get; init; }
    public string? Orientation { get; init; }
    public string? Settings { get; init; }
}

public record UpdateDisplayRequest
{
    public string? Name { get; init; }
    public DisplayType? DisplayType { get; init; }
    public string? DeviceId { get; init; }
    public string? Resolution { get; init; }
    public string? Orientation { get; init; }
    public string? Settings { get; init; }
}

public record CreateDisplayZoneRequest
{
    public string Name { get; init; } = string.Empty;
    public ZoneType ZoneType { get; init; }
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int ZIndex { get; init; }
    public Guid? PlaylistId { get; init; }
    public string? Settings { get; init; }
}

public record UpdateDisplayZoneRequest
{
    public string? Name { get; init; }
    public ZoneType? ZoneType { get; init; }
    public int? PositionX { get; init; }
    public int? PositionY { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? ZIndex { get; init; }
    public Guid? PlaylistId { get; init; }
    public string? Settings { get; init; }
}
