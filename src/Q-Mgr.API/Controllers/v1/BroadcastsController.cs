using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Marketing;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/marketing/broadcasts")]
[Produces("application/json")]
[Authorize]
[RequireModule(ModuleCodes.EngagementCommunications)]
public class BroadcastsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<BroadcastsController> _logger;

    // Same 25MB ceiling ContentController's media upload uses — see that endpoint's comment for
    // why (well under the plan storage quota, pushes larger files toward external links instead).
    // Broadcast attachments have no such external-link alternative, but the individual channels
    // impose their own, often stricter caps anyway (WhatsApp images: 5MB, Telegram photos:
    // 10MB) — this is a shared upper bound, not a promise every channel will accept anything
    // up to it.
    private const long MaxAttachmentSizeBytes = 25 * 1024 * 1024;

    // Keeps a broadcast's total payload sane across every channel — Telegram/WhatsApp send each
    // attachment as its own extra API call per recipient (see NotificationService), so an
    // unbounded count multiplies real per-recipient send time and failure surface linearly.
    private const int MaxAttachmentsPerBroadcast = 5;

    public BroadcastsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        IMediaStorageService mediaStorage,
        ILogger<BroadcastsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    private static BroadcastDto MapToDto(Broadcast b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Channel = b.Channel,
        Subject = b.Subject,
        MessageBody = b.MessageBody,
        AudienceTagFilter = b.AudienceTagFilter,
        Status = b.Status,
        ScheduledAt = b.ScheduledAt,
        SendStartedAt = b.SendStartedAt,
        SendCompletedAt = b.SendCompletedAt,
        TotalRecipients = b.TotalRecipients,
        SentCount = b.SentCount,
        FailedCount = b.FailedCount,
        CreatedAt = b.CreatedAt,
        Attachments = b.Attachments
            .OrderBy(a => a.CreatedAt)
            .Select(a => new BroadcastAttachmentDto
            {
                Id = a.Id,
                Url = a.Url,
                FileName = a.FileName,
                MimeType = a.MimeType,
                FileSizeBytes = a.FileSizeBytes
            })
            .ToList()
    };

    [HttpGet]
    [RequirePermission(Permissions.MarketingView)]
    [ProducesResponseType(typeof(List<BroadcastDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBroadcasts()
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcasts = await _context.Broadcasts
            .Include(b => b.Attachments)
            .Where(b => b.OrganizationId == tenantContext.OrganizationId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return Ok(broadcasts.Select(MapToDto).ToList());
    }

    [HttpGet("{broadcastId:guid}")]
    [RequirePermission(Permissions.MarketingView)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBroadcast(Guid broadcastId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcast = await _context.Broadcasts
            .Include(b => b.Attachments)
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
        if (broadcast == null) return NotFound();

        return Ok(MapToDto(broadcast));
    }

    /// <summary>
    /// Creates a broadcast as a Draft — never sends anything by itself. Sending requires the
    /// separate Schedule action below, which needs MarketingSend, a deliberately higher-stakes
    /// permission than the MarketingManage needed here to just draft one.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.MarketingManage)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateBroadcast([FromBody] CreateBroadcastRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.MessageBody))
            return BadRequest(new ProblemDetails { Title = "Name and message body are required", Status = StatusCodes.Status400BadRequest });

        if (request.Channel == BroadcastChannel.Email && string.IsNullOrWhiteSpace(request.Subject))
            return BadRequest(new ProblemDetails { Title = "Subject is required for email broadcasts", Status = StatusCodes.Status400BadRequest });

        var currentUserId = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var broadcast = new Broadcast
        {
            OrganizationId = tenantContext.OrganizationId,
            Name = request.Name,
            Channel = request.Channel,
            Subject = request.Subject,
            MessageBody = request.MessageBody,
            AudienceTagFilter = request.AudienceTagFilter,
            Status = BroadcastStatus.Draft,
            CreatedByUserId = Guid.TryParse(currentUserId, out var uid) ? uid : Guid.Empty
        };
        _context.Broadcasts.Add(broadcast);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetBroadcast), new { broadcastId = broadcast.Id }, MapToDto(broadcast));
    }

    /// <summary>
    /// Adds one more file to a Draft broadcast (multipart/form-data) — call it once per file for
    /// multiple attachments, up to MaxAttachmentsPerBroadcast. Restricted to Draft — once
    /// scheduled, BroadcastSendJob may already be reading the broadcast, so changing its
    /// attachments out from under it isn't safe. SMS has no attachment concept:
    /// BroadcastSendJob appends each attachment's Url as plain text to the SMS body instead, so
    /// this endpoint doesn't reject SMS-channel broadcasts — there's just a different, degraded
    /// outcome for them.
    /// </summary>
    [HttpPost("{broadcastId:guid}/attachment")]
    [RequirePermission(Permissions.MarketingManage)]
    [RequestSizeLimit(MaxAttachmentSizeBytes)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadAttachment(Guid broadcastId, IFormFile file)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcast = await _context.Broadcasts
            .Include(b => b.Attachments)
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
        if (broadcast == null) return NotFound();

        if (broadcast.Status != BroadcastStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "An attachment can only be added to a Draft broadcast", Status = StatusCodes.Status400BadRequest });

        if (broadcast.Attachments.Count >= MaxAttachmentsPerBroadcast)
            return BadRequest(new ProblemDetails { Title = $"A broadcast can have at most {MaxAttachmentsPerBroadcast} attachments.", Status = StatusCodes.Status400BadRequest });

        if (file == null || file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "No file was provided.", Status = StatusCodes.Status400BadRequest });

        if (file.Length > MaxAttachmentSizeBytes)
            return BadRequest(new ProblemDetails { Title = $"File exceeds the {MaxAttachmentSizeBytes / 1024 / 1024}MB size limit.", Status = StatusCodes.Status400BadRequest });

        // Mirrors ContentController's own media-upload allowlist — the mime types Telegram's
        // sendPhoto/sendDocument and WhatsApp's image/document message types actually accept.
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var mimeType = file.ContentType ?? "";
        var isAllowed = mimeType.StartsWith("image/") || mimeType.StartsWith("video/") ||
                         mimeType.StartsWith("audio/") || mimeType == "application/pdf" || extension == ".pdf";
        if (!isAllowed)
            return BadRequest(new ProblemDetails { Title = $"File type '{(string.IsNullOrEmpty(mimeType) ? extension : mimeType)}' is not allowed.", Status = StatusCodes.Status400BadRequest });

        await using var uploadStream = file.OpenReadStream();
        var uploadResult = await _mediaStorage.UploadAsync(uploadStream, file.FileName, mimeType);
        if (!uploadResult.Success)
        {
            _logger.LogError("Attachment upload failed for broadcast {BroadcastId}, file {FileName}: {Error}", broadcastId, file.FileName, uploadResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the uploaded file." });
        }

        var newAttachment = new BroadcastAttachment
        {
            BroadcastId = broadcast.Id,
            FilePath = uploadResult.FilePath!,
            Url = uploadResult.FileUrl!,
            FileName = file.FileName,
            MimeType = mimeType,
            FileSizeBytes = file.Length
        };
        // Added directly to the DbSet — not via broadcast.Attachments.Add(...) — matching
        // BroadcastSendJob.MaterializeRecipientsAsync's established pattern for the sibling
        // Broadcast->BroadcastRecipient relationship. Adding through the navigation collection
        // on a Broadcast that was just loaded via Include(b => b.Attachments) made EF's change
        // tracker misdetect the new row as Modified rather than Added (a known EF Core quirk
        // with Include-then-Add-to-collection), which then threw DbUpdateConcurrencyException —
        // "expected to affect 1 row, affected 0" — deterministically on every attachment upload.
        _context.BroadcastAttachments.Add(newAttachment);
        broadcast.Attachments.Add(newAttachment);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Attached {FileName} ({SizeBytes} bytes) to broadcast {BroadcastId}", file.FileName, file.Length, broadcastId);

        return Ok(MapToDto(broadcast));
    }

    [HttpDelete("{broadcastId:guid}/attachment/{attachmentId:guid}")]
    [RequirePermission(Permissions.MarketingManage)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteAttachment(Guid broadcastId, Guid attachmentId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var broadcast = await _context.Broadcasts
            .Include(b => b.Attachments)
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
        if (broadcast == null) return NotFound();

        var attachment = broadcast.Attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment == null) return NotFound();

        if (broadcast.Status != BroadcastStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "An attachment can only be removed from a Draft broadcast", Status = StatusCodes.Status400BadRequest });

        await _mediaStorage.DeleteAsync(attachment.FilePath);
        broadcast.Attachments.Remove(attachment);
        _context.BroadcastAttachments.Remove(attachment);
        await _context.SaveChangesAsync();

        return Ok(MapToDto(broadcast));
    }

    /// <summary>
    /// Schedules a Draft broadcast to send — either immediately (next job tick, within ~60s)
    /// or at a future ScheduledAt. This is the one action that actually causes messages to go
    /// out, which is why it's gated on MarketingSend rather than MarketingManage.
    /// </summary>
    [HttpPost("{broadcastId:guid}/schedule")]
    [RequirePermission(Permissions.MarketingSend)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ScheduleBroadcast(Guid broadcastId, [FromBody] ScheduleBroadcastRequest? request = null)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var affected = await _context.Broadcasts
            .Where(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId && b.Status == BroadcastStatus.Draft)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BroadcastStatus.Scheduled)
                .SetProperty(b => b.ScheduledAt, request != null && request.ScheduledAt.HasValue ? request.ScheduledAt : DateTime.UtcNow)
                .SetProperty(b => b.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Broadcasts.AnyAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails { Title = "Only a Draft broadcast can be scheduled", Status = StatusCodes.Status400BadRequest });
        }

        var broadcast = await _context.Broadcasts.Include(b => b.Attachments).FirstAsync(b => b.Id == broadcastId);
        return Ok(MapToDto(broadcast));
    }

    [HttpPost("{broadcastId:guid}/cancel")]
    [RequirePermission(Permissions.MarketingSend)]
    [ProducesResponseType(typeof(BroadcastDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelBroadcast(Guid broadcastId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        // Only a still-Scheduled broadcast can be cancelled — once the job has claimed it
        // (Status = Sending) it may already have sent some recipients, so "cancel" would be
        // misleading at that point.
        var affected = await _context.Broadcasts
            .Where(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId && b.Status == BroadcastStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BroadcastStatus.Cancelled)
                .SetProperty(b => b.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Broadcasts.AnyAsync(b => b.Id == broadcastId && b.OrganizationId == tenantContext.OrganizationId);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails { Title = "Only a Scheduled broadcast (not yet started sending) can be cancelled", Status = StatusCodes.Status400BadRequest });
        }

        var broadcast = await _context.Broadcasts.Include(b => b.Attachments).FirstAsync(b => b.Id == broadcastId);
        return Ok(MapToDto(broadcast));
    }
}

public record ScheduleBroadcastRequest
{
    public DateTime? ScheduledAt { get; init; }
}
