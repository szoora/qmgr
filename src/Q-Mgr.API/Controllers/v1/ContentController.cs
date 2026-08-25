using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.API.Hubs;
using QMgr.Filters;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public class ContentController : ControllerBase
{
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<ContentController> _logger;
    private readonly IDisplayHubContext _displayHub;
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly IMediaStorageService _mediaStorage;
    private readonly IUsageTrackingService _usageTracking;

    public ContentController(
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        ILogger<ContentController> logger,
        IDisplayHubContext displayHub,
        IWebHostEnvironment webHostEnvironment,
        IMediaStorageService mediaStorage,
        IUsageTrackingService usageTracking)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
        _displayHub = displayHub;
        _webHostEnvironment = webHostEnvironment;
        _mediaStorage = mediaStorage;
        _usageTracking = usageTracking;
    }

    /// <summary>
    /// Recomputes an organization's total uploaded-file storage from its actual MediaContent
    /// rows and pushes the new total to the usage-tracking snapshot. Called after any upload
    /// or delete so the storage quota check always reflects reality — UpdateStorageUsageAsync
    /// sets an absolute value (like ActiveUsers/ActiveBranches), it doesn't increment, so a
    /// stale delta would silently drift over time if a caller ever passed one.
    /// </summary>
    private async Task RecalculateStorageUsageAsync(Guid organizationId)
    {
        var totalBytes = await _dbContext.MediaContents
            .Where(m => m.OrganizationId == organizationId)
            .SumAsync(m => (long?)m.FileSizeBytes) ?? 0;

        await _usageTracking.UpdateStorageUsageAsync(organizationId, totalBytes);
    }

    /// <summary>
    /// SECURITY: Playlist/Display/DisplayZone have no global EF query filter (unlike
    /// MediaContent), so every action that reaches one by ID must verify branch ownership
    /// explicitly — matches TokensController.VerifyBranchOwnership, with a SuperAdmin bypass
    /// added since content authoring is a normal tenant-admin action, not platform-only.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// SECURITY: never trust a client-supplied organizationId for a create/upload — MediaContent
    /// is protected on reads by its own EF query filter, but inserts bypass that filter entirely
    /// (same class of gap fixed on RolesController/NotificationsController this session).
    /// </summary>
    private Guid? ResolveOrganizationIdForWrite(Guid requestedOrganizationId, out IActionResult? error)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (RoleCodes.IsSuperAdmin(tenantContext?.UserRole))
        {
            error = null;
            return requestedOrganizationId;
        }

        if (tenantContext == null || !tenantContext.IsResolved)
        {
            error = Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });
            return null;
        }

        error = null;
        return tenantContext.OrganizationId;
    }

    #region Media Content

    /// <summary>
    /// Gets all media content for an organization
    /// </summary>
    [HttpGet("organizations/{organizationId:guid}/media")]
    [RequirePermission(Permissions.ContentView)]
    [ProducesResponseType(typeof(List<MediaContentDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMediaContents(Guid organizationId)
    {
        var media = await _dbContext.MediaContents
            .Where(m => m.OrganizationId == organizationId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MediaContentDto
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                ContentType = m.ContentType,
                MimeType = m.MimeType,
                FileUrl = m.FileUrl,
                ThumbnailUrl = m.ThumbnailUrl,
                FileSizeBytes = m.FileSizeBytes,
                DurationSeconds = m.DurationSeconds,
                Tags = m.Tags,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync();

        return Ok(media);
    }

    /// <summary>
    /// Gets a specific media content by ID
    /// </summary>
    [HttpGet("media/{mediaId:guid}")]
    [AllowAnonymous] // Public for display screens
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMediaContent(Guid mediaId)
    {
        var media = await _dbContext.MediaContents
            .Where(m => m.Id == mediaId)
            .Select(m => new MediaContentDto
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                ContentType = m.ContentType,
                MimeType = m.MimeType,
                FileUrl = m.FileUrl,
                ThumbnailUrl = m.ThumbnailUrl,
                FileSizeBytes = m.FileSizeBytes,
                DurationSeconds = m.DurationSeconds,
                Tags = m.Tags,
                CreatedAt = m.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (media == null)
            return NotFound();

        return Ok(media);
    }

    /// <summary>
    /// Creates new media content
    /// </summary>
    [HttpPost("organizations/{organizationId:guid}/media")]
    [RequirePermission(Permissions.ContentCreate)]
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateMediaContent(Guid organizationId, [FromBody] CreateMediaContentRequest request)
    {
        var resolvedOrgId = ResolveOrganizationIdForWrite(organizationId, out var orgError);
        if (orgError != null) return orgError;

        var media = new MediaContent
        {
            OrganizationId = resolvedOrgId!.Value,
            Name = request.Name,
            Description = request.Description,
            ContentType = request.ContentType,
            MimeType = request.MimeType,
            StorageType = request.StorageType,
            FileUrl = request.FileUrl,
            ThumbnailUrl = request.ThumbnailUrl,
            FileSizeBytes = request.FileSizeBytes,
            DurationSeconds = request.DurationSeconds,
            TextContent = request.TextContent,
            Tags = request.Tags
        };

        _dbContext.MediaContents.Add(media);
        await _dbContext.SaveChangesAsync();

        var dto = new MediaContentDto
        {
            Id = media.Id,
            Name = media.Name,
            Description = media.Description,
            ContentType = media.ContentType,
            MimeType = media.MimeType,
            FileUrl = media.FileUrl,
            ThumbnailUrl = media.ThumbnailUrl,
            FileSizeBytes = media.FileSizeBytes,
            DurationSeconds = media.DurationSeconds,
            Tags = media.Tags,
            CreatedAt = media.CreatedAt
        };

        return CreatedAtAction(nameof(GetMediaContent), new { mediaId = media.Id }, dto);
    }

    // Deliberately well under the default 100MB tenant storage quota (SubscriptionPlan.MaxStorageMb)
    // so a single upload can't consume most/all of it — pushes larger media toward linking an
    // external platform (YouTube, Vimeo, Google Drive, TikTok) instead. Matches MediaLibrary.razor's
    // client-side MaxFileSize, which is a UX hint only; this is the real enforced boundary.
    private const long MaxUploadSizeBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Uploads a real media file (multipart/form-data) and creates its content record in one
    /// step. Previously the only way to create a MediaContent row was CreateMediaContent above,
    /// which just accepts a JSON FileUrl string — actual file bytes had to be written directly
    /// to the Blazor Web app's own local disk by the caller (MediaLibrary.razor), so uploaded
    /// files couldn't survive or be reachable if the Web app ever ran as multiple instances.
    /// Storing here instead means the API — not whichever Web instance happened to handle the
    /// request — is the single, canonical place uploaded content lives.
    /// </summary>
    [HttpPost("organizations/{organizationId:guid}/media/upload")]
    [RequirePermission(Permissions.ContentCreate)]
    [RequestSizeLimit(MaxUploadSizeBytes)]
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadMediaContent(
        Guid organizationId,
        IFormFile file,
        [FromForm] string? name,
        [FromForm] string? description,
        [FromForm] string[]? tags)
    {
        var resolvedOrgId = ResolveOrganizationIdForWrite(organizationId, out var orgError);
        if (orgError != null) return orgError;

        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file was provided." });

        if (file.Length > MaxUploadSizeBytes)
            return BadRequest(new { message = $"File exceeds the {MaxUploadSizeBytes / 1024 / 1024}MB size limit." });

        // Mirrors MediaLibrary.razor's own <InputFile accept="image/*,video/*,audio/*,.pdf">.
        // That's a client-side hint only — the browser doesn't enforce it — so this is the
        // real boundary. PowerPoint is deliberately not accepted here either: its rendering
        // path is separately known-broken (see docs/TASK_TRACKER.md Phase 7) and re-exposing
        // upload for a format that can't be viewed yet would just be a confusing dead end.
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var mimeType = file.ContentType ?? "";
        var isAllowed = mimeType.StartsWith("image/") || mimeType.StartsWith("video/") ||
                         mimeType.StartsWith("audio/") || (mimeType == "application/pdf" || extension == ".pdf");

        if (!isAllowed)
            return BadRequest(new { message = $"File type '{(string.IsNullOrEmpty(mimeType) ? extension : mimeType)}' is not allowed." });

        // Storage-quota enforcement: this project deliberately keeps as little content as
        // possible on local/server disk (see the platform's storage-conservation direction) —
        // linking existing external platforms (YouTube, Vimeo, Google Drive, TikTok, Spotify)
        // via Add URL is unlimited and the preferred path; direct uploads are capped per plan.
        var storageStatus = await _usageTracking.GetLimitStatusAsync(resolvedOrgId.Value, "storage");
        var maxStorageBytes = (long)storageStatus.MaxAllowed * 1024 * 1024;
        var currentStorageBytes = (long)storageStatus.CurrentUsage * 1024 * 1024;
        if (currentStorageBytes + file.Length > maxStorageBytes)
            return BadRequest(new
            {
                message = $"This upload would exceed your organization's storage quota ({storageStatus.MaxAllowed}MB). " +
                           "Consider linking the file from YouTube, Vimeo, Google Drive, TikTok, or SoundCloud instead " +
                           "of uploading it (use \"Add URL\") — linked content doesn't count against your quota. " +
                           "Contact your platform admin if you need more storage.",
                storageQuotaExceeded = true
            });

        var contentType = GetContentTypeFromMime(mimeType, extension);

        await using var uploadStream = file.OpenReadStream();
        var uploadResult = await _mediaStorage.UploadAsync(uploadStream, file.FileName, mimeType);
        if (!uploadResult.Success)
        {
            _logger.LogError("Media storage upload failed for {FileName}: {Error}", file.FileName, uploadResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to store the uploaded file." });
        }

        var fileUrl = uploadResult.FileUrl!;

        var media = new MediaContent
        {
            OrganizationId = resolvedOrgId!.Value,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file.FileName) : name,
            Description = description,
            ContentType = contentType,
            MimeType = mimeType,
            StorageType = StorageType.Local,
            FilePath = uploadResult.FilePath,
            FileUrl = fileUrl,
            ThumbnailUrl = contentType == ContentType.Image ? fileUrl : null,
            FileSizeBytes = file.Length,
            Tags = tags
        };

        _dbContext.MediaContents.Add(media);
        await _dbContext.SaveChangesAsync();
        await RecalculateStorageUsageAsync(resolvedOrgId.Value);

        _logger.LogInformation("Uploaded media {MediaId} ({FileName}, {SizeBytes} bytes) for organization {OrganizationId}",
            media.Id, file.FileName, file.Length, resolvedOrgId.Value);

        var dto = new MediaContentDto
        {
            Id = media.Id,
            Name = media.Name,
            Description = media.Description,
            ContentType = media.ContentType,
            MimeType = media.MimeType,
            FileUrl = media.FileUrl,
            ThumbnailUrl = media.ThumbnailUrl,
            FileSizeBytes = media.FileSizeBytes,
            DurationSeconds = media.DurationSeconds,
            Tags = media.Tags,
            CreatedAt = media.CreatedAt
        };

        return CreatedAtAction(nameof(GetMediaContent), new { mediaId = media.Id }, dto);
    }

    private static ContentType GetContentTypeFromMime(string mimeType, string extension)
    {
        if (mimeType.StartsWith("image/")) return ContentType.Image;
        if (mimeType.StartsWith("video/")) return ContentType.Video;
        if (mimeType.StartsWith("audio/")) return ContentType.Audio;
        if (mimeType == "application/pdf" || extension == ".pdf") return ContentType.Pdf;
        return ContentType.Image;
    }

    /// <summary>
    /// Updates media content
    /// </summary>
    [HttpPut("media/{mediaId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(MediaContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMediaContent(Guid mediaId, [FromBody] UpdateMediaContentRequest request)
    {
        var media = await _dbContext.MediaContents.FindAsync(mediaId);
        if (media == null)
            return NotFound();

        media.Name = request.Name ?? media.Name;
        media.Description = request.Description;
        media.Tags = request.Tags ?? media.Tags;

        await _dbContext.SaveChangesAsync();

        var dto = new MediaContentDto
        {
            Id = media.Id,
            Name = media.Name,
            Description = media.Description,
            ContentType = media.ContentType,
            MimeType = media.MimeType,
            FileUrl = media.FileUrl,
            ThumbnailUrl = media.ThumbnailUrl,
            FileSizeBytes = media.FileSizeBytes,
            DurationSeconds = media.DurationSeconds,
            Tags = media.Tags,
            CreatedAt = media.CreatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Deletes media content
    /// </summary>
    [HttpDelete("media/{mediaId:guid}")]
    [RequirePermission(Permissions.ContentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMediaContent(Guid mediaId)
    {
        var media = await _dbContext.MediaContents.FindAsync(mediaId);
        if (media == null)
            return NotFound();

        _dbContext.MediaContents.Remove(media);
        await _dbContext.SaveChangesAsync();
        await RecalculateStorageUsageAsync(media.OrganizationId);

        // Found while wiring this endpoint through IMediaStorageService: the physical file
        // was never actually deleted before, only the DB row — a disk-space leak on every
        // delete. Best-effort: the DB row is already gone either way, and older rows may
        // have a null FilePath (uploaded before this field was populated) with nothing to
        // clean up on disk.
        if (!string.IsNullOrEmpty(media.FilePath))
        {
            var deleted = await _mediaStorage.DeleteAsync(media.FilePath);
            if (!deleted)
                _logger.LogWarning("Deleted media record {MediaId} but failed to delete its underlying file {FilePath}", mediaId, media.FilePath);
        }

        return NoContent();
    }

    #endregion

    #region Playlists

    /// <summary>
    /// Gets all playlists for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/playlists")]
    [RequirePermission(Permissions.ContentView)]
    [ProducesResponseType(typeof(List<PlaylistDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlaylists(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var playlists = await _dbContext.Playlists
            .Where(p => p.BranchId == branchId)
            .Include(p => p.Items)
            .ThenInclude(i => i.MediaContent)
            .OrderBy(p => p.Name)
            .Select(p => new PlaylistDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                ScheduleType = p.ScheduleType,
                TransitionType = p.TransitionType,
                DefaultDurationSeconds = p.DefaultDurationSeconds,
                Loop = p.Loop,
                Shuffle = p.Shuffle,
                SpotifyPlaylistId = p.SpotifyPlaylistId,
                SpotifyPlaylistName = p.SpotifyPlaylistName,
                ItemCount = p.Items.Count,
                CreatedAt = p.CreatedAt
            })
            .ToListAsync();

        return Ok(playlists);
    }

    /// <summary>
    /// Gets a specific playlist with items
    /// </summary>
    [HttpGet("playlists/{playlistId:guid}")]
    [AllowAnonymous] // Public for display screens
    [ProducesResponseType(typeof(PlaylistDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPlaylist(Guid playlistId)
    {
        var playlist = await _dbContext.Playlists
            .Where(p => p.Id == playlistId)
            .Include(p => p.Items.OrderBy(i => i.Position))
            .ThenInclude(i => i.MediaContent)
            .Include(p => p.Items)
            .ThenInclude(i => i.Campaign)
            .FirstOrDefaultAsync();

        if (playlist == null)
            return NotFound();

        var now = DateTime.UtcNow;

        var dto = new PlaylistDetailDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            ScheduleType = playlist.ScheduleType,
            Schedule = playlist.Schedule,
            TransitionType = playlist.TransitionType,
            DefaultDurationSeconds = playlist.DefaultDurationSeconds,
            Loop = playlist.Loop,
            Shuffle = playlist.Shuffle,
            SpotifyPlaylistId = playlist.SpotifyPlaylistId,
            SpotifyPlaylistName = playlist.SpotifyPlaylistName,
            Items = playlist.Items.Select(i => new PlaylistItemDto
            {
                Id = i.Id,
                MediaContentId = i.MediaContentId,
                MediaName = i.MediaContent?.Name ?? "",
                MediaType = i.MediaContent?.ContentType ?? ContentType.Image,
                FileUrl = i.MediaContent?.FileUrl,
                ThumbnailUrl = i.MediaContent?.ThumbnailUrl,
                DurationSeconds = i.DurationSeconds ?? 10,
                Position = i.Position,
                CampaignId = i.CampaignId,
                CampaignActive = i.Campaign != null && i.Campaign.IsActive && i.Campaign.StartDate <= now && i.Campaign.EndDate >= now
            }).ToList(),
            ItemCount = playlist.Items.Count,
            CreatedAt = playlist.CreatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Creates a new playlist
    /// </summary>
    [HttpPost("branches/{branchId:guid}/playlists")]
    [RequirePermission(Permissions.ContentCreate)]
    [ProducesResponseType(typeof(PlaylistDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreatePlaylist(Guid branchId, [FromBody] CreatePlaylistRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var playlist = new Playlist
        {
            BranchId = branchId,
            Name = request.Name,
            Description = request.Description,
            ScheduleType = request.ScheduleType ?? "always",
            Schedule = request.Schedule,
            TransitionType = request.TransitionType ?? "fade",
            DefaultDurationSeconds = request.DefaultDurationSeconds ?? 10,
            Loop = request.Loop ?? true,
            Shuffle = request.Shuffle ?? false
        };

        _dbContext.Playlists.Add(playlist);
        await _dbContext.SaveChangesAsync();

        var dto = new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            ScheduleType = playlist.ScheduleType,
            TransitionType = playlist.TransitionType,
            DefaultDurationSeconds = playlist.DefaultDurationSeconds,
            Loop = playlist.Loop,
            Shuffle = playlist.Shuffle,
            SpotifyPlaylistId = playlist.SpotifyPlaylistId,
            SpotifyPlaylistName = playlist.SpotifyPlaylistName,
            ItemCount = 0,
            CreatedAt = playlist.CreatedAt
        };

        return CreatedAtAction(nameof(GetPlaylist), new { playlistId = playlist.Id }, dto);
    }

    /// <summary>
    /// Updates a playlist
    /// </summary>
    [HttpPut("playlists/{playlistId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(PlaylistDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdatePlaylist(Guid playlistId, [FromBody] UpdatePlaylistRequest request)
    {
        var playlist = await _dbContext.Playlists.FindAsync(playlistId);
        if (playlist == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(playlist.BranchId);
        if (branchError != null) return branchError;

        playlist.Name = request.Name ?? playlist.Name;
        playlist.Description = request.Description ?? playlist.Description;
        playlist.ScheduleType = request.ScheduleType ?? playlist.ScheduleType;
        playlist.Schedule = request.Schedule ?? playlist.Schedule;
        playlist.TransitionType = request.TransitionType ?? playlist.TransitionType;
        playlist.DefaultDurationSeconds = request.DefaultDurationSeconds ?? playlist.DefaultDurationSeconds;
        playlist.Loop = request.Loop ?? playlist.Loop;
        playlist.Shuffle = request.Shuffle ?? playlist.Shuffle;

        await _dbContext.SaveChangesAsync();

        var dto = new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            ScheduleType = playlist.ScheduleType,
            TransitionType = playlist.TransitionType,
            DefaultDurationSeconds = playlist.DefaultDurationSeconds,
            Loop = playlist.Loop,
            Shuffle = playlist.Shuffle,
            SpotifyPlaylistId = playlist.SpotifyPlaylistId,
            SpotifyPlaylistName = playlist.SpotifyPlaylistName,
            ItemCount = await _dbContext.PlaylistItems.CountAsync(i => i.PlaylistId == playlistId),
            CreatedAt = playlist.CreatedAt
        };

        await _displayHub.UpdatePlaylistContent(playlist.BranchId, dto);

        return Ok(dto);
    }

    /// <summary>
    /// Sets or clears a playlist's Spotify background-music playlist. Separate
    /// from UpdatePlaylist because that endpoint treats a null field as "don't
    /// change" — there'd be no way to explicitly clear the selection through it.
    /// </summary>
    [HttpPut("playlists/{playlistId:guid}/spotify-background")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(PlaylistDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPlaylistSpotifyBackground(Guid playlistId, [FromBody] SetPlaylistSpotifyBackgroundRequest request)
    {
        var playlist = await _dbContext.Playlists.FindAsync(playlistId);
        if (playlist == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(playlist.BranchId);
        if (branchError != null) return branchError;

        playlist.SpotifyPlaylistId = request.SpotifyPlaylistId;
        playlist.SpotifyPlaylistName = request.SpotifyPlaylistName;

        await _dbContext.SaveChangesAsync();

        var dto = new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            ScheduleType = playlist.ScheduleType,
            TransitionType = playlist.TransitionType,
            DefaultDurationSeconds = playlist.DefaultDurationSeconds,
            Loop = playlist.Loop,
            Shuffle = playlist.Shuffle,
            SpotifyPlaylistId = playlist.SpotifyPlaylistId,
            SpotifyPlaylistName = playlist.SpotifyPlaylistName,
            ItemCount = await _dbContext.PlaylistItems.CountAsync(i => i.PlaylistId == playlistId),
            CreatedAt = playlist.CreatedAt
        };

        await _displayHub.UpdatePlaylistContent(playlist.BranchId, dto);

        return Ok(dto);
    }

    /// <summary>
    /// Deletes a playlist
    /// </summary>
    [HttpDelete("playlists/{playlistId:guid}")]
    [RequirePermission(Permissions.ContentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePlaylist(Guid playlistId)
    {
        var playlist = await _dbContext.Playlists.FindAsync(playlistId);
        if (playlist == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(playlist.BranchId);
        if (branchError != null) return branchError;

        _dbContext.Playlists.Remove(playlist);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Adds an item to a playlist
    /// </summary>
    [HttpPost("playlists/{playlistId:guid}/items")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(PlaylistItemDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddPlaylistItem(Guid playlistId, [FromBody] AddPlaylistItemRequest request)
    {
        var playlist = await _dbContext.Playlists.FindAsync(playlistId);
        if (playlist == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(playlist.BranchId);
        if (branchError != null) return branchError;

        Campaign? campaign = null;
        if (request.CampaignId.HasValue)
        {
            campaign = await _dbContext.Campaigns.FindAsync(request.CampaignId.Value);
            if (campaign == null || campaign.BranchId != playlist.BranchId)
                return BadRequest(new ProblemDetails { Title = "Invalid campaign", Detail = "Campaign does not belong to this playlist's branch.", Status = StatusCodes.Status400BadRequest });
        }

        var maxPosition = await _dbContext.PlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .MaxAsync(i => (int?)i.Position) ?? 0;

        var item = new PlaylistItem
        {
            PlaylistId = playlistId,
            MediaContentId = request.MediaContentId,
            DurationSeconds = request.DurationSeconds ?? playlist.DefaultDurationSeconds,
            Position = request.Position ?? maxPosition + 1,
            CampaignId = request.CampaignId
        };

        _dbContext.PlaylistItems.Add(item);
        await _dbContext.SaveChangesAsync();

        var media = await _dbContext.MediaContents.FindAsync(request.MediaContentId);

        var dto = new PlaylistItemDto
        {
            Id = item.Id,
            MediaContentId = item.MediaContentId,
            MediaName = media?.Name ?? "",
            MediaType = media?.ContentType ?? ContentType.Image,
            FileUrl = media?.FileUrl,
            ThumbnailUrl = media?.ThumbnailUrl,
            DurationSeconds = item.DurationSeconds ?? 10,
            CampaignId = item.CampaignId,
            CampaignActive = campaign != null && campaign.IsActive && campaign.StartDate <= DateTime.UtcNow && campaign.EndDate >= DateTime.UtcNow,
            Position = item.Position
        };

        await _displayHub.UpdatePlaylistContent(playlist.BranchId, new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            ScheduleType = playlist.ScheduleType,
            TransitionType = playlist.TransitionType,
            DefaultDurationSeconds = playlist.DefaultDurationSeconds,
            Loop = playlist.Loop,
            Shuffle = playlist.Shuffle,
            SpotifyPlaylistId = playlist.SpotifyPlaylistId,
            SpotifyPlaylistName = playlist.SpotifyPlaylistName,
            ItemCount = await _dbContext.PlaylistItems.CountAsync(i => i.PlaylistId == playlistId),
            CreatedAt = playlist.CreatedAt
        });

        return Created($"/api/v1/playlists/{playlistId}/items/{item.Id}", dto);
    }

    /// <summary>
    /// Removes an item from a playlist
    /// </summary>
    [HttpDelete("playlists/{playlistId:guid}/items/{itemId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemovePlaylistItem(Guid playlistId, Guid itemId)
    {
        var ownerPlaylist = await _dbContext.Playlists.FindAsync(playlistId);
        if (ownerPlaylist == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(ownerPlaylist.BranchId);
        if (branchError != null) return branchError;

        var item = await _dbContext.PlaylistItems
            .Where(i => i.Id == itemId && i.PlaylistId == playlistId)
            .FirstOrDefaultAsync();

        if (item == null)
            return NotFound();

        _dbContext.PlaylistItems.Remove(item);
        await _dbContext.SaveChangesAsync();

        var playlist = await _dbContext.Playlists.FindAsync(playlistId);
        if (playlist != null)
        {
            await _displayHub.UpdatePlaylistContent(playlist.BranchId, new PlaylistDto
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Description = playlist.Description,
                ScheduleType = playlist.ScheduleType,
                TransitionType = playlist.TransitionType,
                DefaultDurationSeconds = playlist.DefaultDurationSeconds,
                Loop = playlist.Loop,
                Shuffle = playlist.Shuffle,
                SpotifyPlaylistId = playlist.SpotifyPlaylistId,
                SpotifyPlaylistName = playlist.SpotifyPlaylistName,
                ItemCount = await _dbContext.PlaylistItems.CountAsync(i => i.PlaylistId == playlistId),
                CreatedAt = playlist.CreatedAt
            });
        }

        return NoContent();
    }

    #endregion

    #region Displays

    /// <summary>
    /// Gets all displays for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/displays")]
    [RequirePermission(Permissions.ContentView)]
    [ProducesResponseType(typeof(List<DisplayDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDisplays(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var displays = await _dbContext.Displays
            .Where(d => d.BranchId == branchId)
            .Include(d => d.DisplayZones)
            .OrderBy(d => d.Name)
            .Select(d => new DisplayDto
            {
                Id = d.Id,
                Name = d.Name,
                DisplayType = d.DisplayType,
                DeviceId = d.DeviceId,
                Resolution = d.Resolution,
                Orientation = d.Orientation,
                Status = d.Status,
                LastHeartbeat = d.LastHeartbeat,
                ZoneCount = d.DisplayZones.Count,
                CreatedAt = d.CreatedAt
            })
            .ToListAsync();

        return Ok(displays);
    }

    /// <summary>
    /// Gets a specific display with zones
    /// </summary>
    [HttpGet("displays/{displayId:guid}")]
    [AllowAnonymous] // Public for display screens
    [ProducesResponseType(typeof(DisplayDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDisplay(Guid displayId)
    {
        var display = await _dbContext.Displays
            .Where(d => d.Id == displayId)
            .Include(d => d.DisplayZones)
            .ThenInclude(z => z.Playlist)
            .FirstOrDefaultAsync();

        if (display == null)
            return NotFound();

        var dto = new DisplayDetailDto
        {
            Id = display.Id,
            Name = display.Name,
            DisplayType = display.DisplayType,
            DeviceId = display.DeviceId,
            Resolution = display.Resolution,
            Orientation = display.Orientation,
            Status = display.Status,
            LastHeartbeat = display.LastHeartbeat,
            Settings = display.Settings,
            Zones = display.DisplayZones.Select(z => new DisplayZoneDto
            {
                Id = z.Id,
                Name = z.Name,
                ZoneType = z.ZoneType,
                PositionX = z.PositionX,
                PositionY = z.PositionY,
                Width = z.Width,
                Height = z.Height,
                ZIndex = z.ZIndex,
                PlaylistId = z.PlaylistId,
                PlaylistName = z.Playlist?.Name
            }).ToList(),
            CreatedAt = display.CreatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Creates a new display
    /// </summary>
    [HttpPost("branches/{branchId:guid}/displays")]
    [RequirePermission(Permissions.ContentCreate)]
    [CheckLimit("displays")]
    [ProducesResponseType(typeof(DisplayDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateDisplay(Guid branchId, [FromBody] CreateDisplayRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var display = new Display
        {
            BranchId = branchId,
            Name = request.Name,
            DisplayType = request.DisplayType,
            DeviceId = request.DeviceId,
            Resolution = request.Resolution,
            Orientation = request.Orientation ?? "landscape",
            Status = "offline",
            Settings = request.Settings
        };

        _dbContext.Displays.Add(display);
        await _dbContext.SaveChangesAsync();

        var dto = new DisplayDto
        {
            Id = display.Id,
            Name = display.Name,
            DisplayType = display.DisplayType,
            DeviceId = display.DeviceId,
            Resolution = display.Resolution,
            Orientation = display.Orientation,
            Status = display.Status,
            LastHeartbeat = display.LastHeartbeat,
            ZoneCount = 0,
            CreatedAt = display.CreatedAt
        };

        return CreatedAtAction(nameof(GetDisplay), new { displayId = display.Id }, dto);
    }

    /// <summary>
    /// Updates a display
    /// </summary>
    [HttpPut("displays/{displayId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(DisplayDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDisplay(Guid displayId, [FromBody] UpdateDisplayRequest request)
    {
        var display = await _dbContext.Displays.FindAsync(displayId);
        if (display == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(display.BranchId);
        if (branchError != null) return branchError;

        display.Name = request.Name ?? display.Name;
        display.DisplayType = request.DisplayType ?? display.DisplayType;
        display.DeviceId = request.DeviceId ?? display.DeviceId;
        display.Resolution = request.Resolution ?? display.Resolution;
        display.Orientation = request.Orientation ?? display.Orientation;
        display.Settings = request.Settings ?? display.Settings;

        await _dbContext.SaveChangesAsync();

        var dto = new DisplayDto
        {
            Id = display.Id,
            Name = display.Name,
            DisplayType = display.DisplayType,
            DeviceId = display.DeviceId,
            Resolution = display.Resolution,
            Orientation = display.Orientation,
            Status = display.Status,
            LastHeartbeat = display.LastHeartbeat,
            ZoneCount = await _dbContext.DisplayZones.CountAsync(z => z.DisplayId == displayId),
            CreatedAt = display.CreatedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Deletes a display
    /// </summary>
    [HttpDelete("displays/{displayId:guid}")]
    [RequirePermission(Permissions.ContentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDisplay(Guid displayId)
    {
        var display = await _dbContext.Displays.FindAsync(displayId);
        if (display == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(display.BranchId);
        if (branchError != null) return branchError;

        _dbContext.Displays.Remove(display);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    #endregion

    #region Display Zones

    /// <summary>
    /// Creates a new display zone
    /// </summary>
    [HttpPost("displays/{displayId:guid}/zones")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(DisplayZoneDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateDisplayZone(Guid displayId, [FromBody] CreateDisplayZoneRequest request)
    {
        var display = await _dbContext.Displays.FindAsync(displayId);
        if (display == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(display.BranchId);
        if (branchError != null) return branchError;

        var zone = new DisplayZone
        {
            DisplayId = displayId,
            Name = request.Name,
            ZoneType = request.ZoneType,
            PositionX = request.PositionX,
            PositionY = request.PositionY,
            Width = request.Width,
            Height = request.Height,
            ZIndex = request.ZIndex,
            PlaylistId = request.PlaylistId,
            Settings = request.Settings
        };

        _dbContext.DisplayZones.Add(zone);
        await _dbContext.SaveChangesAsync();

        var dto = new DisplayZoneDto
        {
            Id = zone.Id,
            Name = zone.Name,
            ZoneType = zone.ZoneType,
            PositionX = zone.PositionX,
            PositionY = zone.PositionY,
            Width = zone.Width,
            Height = zone.Height,
            ZIndex = zone.ZIndex,
            PlaylistId = zone.PlaylistId
        };

        return Created($"/api/v1/displays/{displayId}/zones/{zone.Id}", dto);
    }

    /// <summary>
    /// Updates a display zone
    /// </summary>
    [HttpPut("displays/{displayId:guid}/zones/{zoneId:guid}")]
    [RequirePermission(Permissions.ContentEdit)]
    [ProducesResponseType(typeof(DisplayZoneDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDisplayZone(Guid displayId, Guid zoneId, [FromBody] UpdateDisplayZoneRequest request)
    {
        var parentDisplay = await _dbContext.Displays.FindAsync(displayId);
        if (parentDisplay == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(parentDisplay.BranchId);
        if (branchError != null) return branchError;

        var zone = await _dbContext.DisplayZones
            .Where(z => z.Id == zoneId && z.DisplayId == displayId)
            .FirstOrDefaultAsync();

        if (zone == null)
            return NotFound();

        zone.Name = request.Name ?? zone.Name;
        zone.ZoneType = request.ZoneType ?? zone.ZoneType;
        zone.PositionX = request.PositionX ?? zone.PositionX;
        zone.PositionY = request.PositionY ?? zone.PositionY;
        zone.Width = request.Width ?? zone.Width;
        zone.Height = request.Height ?? zone.Height;
        zone.ZIndex = request.ZIndex ?? zone.ZIndex;
        zone.PlaylistId = request.PlaylistId ?? zone.PlaylistId;
        zone.Settings = request.Settings ?? zone.Settings;

        await _dbContext.SaveChangesAsync();

        var dto = new DisplayZoneDto
        {
            Id = zone.Id,
            Name = zone.Name,
            ZoneType = zone.ZoneType,
            PositionX = zone.PositionX,
            PositionY = zone.PositionY,
            Width = zone.Width,
            Height = zone.Height,
            ZIndex = zone.ZIndex,
            PlaylistId = zone.PlaylistId
        };

        return Ok(dto);
    }

    /// <summary>
    /// Deletes a display zone
    /// </summary>
    [HttpDelete("displays/{displayId:guid}/zones/{zoneId:guid}")]
    [RequirePermission(Permissions.ContentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDisplayZone(Guid displayId, Guid zoneId)
    {
        var parentDisplay = await _dbContext.Displays.FindAsync(displayId);
        if (parentDisplay == null)
            return NotFound();

        var branchError = await VerifyBranchOwnership(parentDisplay.BranchId);
        if (branchError != null) return branchError;

        var zone = await _dbContext.DisplayZones
            .Where(z => z.Id == zoneId && z.DisplayId == displayId)
            .FirstOrDefaultAsync();

        if (zone == null)
            return NotFound();

        _dbContext.DisplayZones.Remove(zone);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    #endregion
}

// DTOs for this controller live in QMgr.Application.DTOs (ContentDto.cs) —
// single source of truth, shared with anything else in the API layer that
// needs them, instead of duplicated inline here.
