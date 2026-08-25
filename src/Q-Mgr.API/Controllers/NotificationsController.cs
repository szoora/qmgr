using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Controllers;

/// <summary>
/// Controller for managing notifications
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly INotificationSettingsService _settingsService;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly QMgrDbContext _dbContext;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationService notificationService,
        INotificationSettingsService settingsService,
        ITenantContextAccessor tenantAccessor,
        QMgrDbContext dbContext,
        ILogger<NotificationsController> logger)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;
        _tenantAccessor = tenantAccessor;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// SECURITY: notification settings carry live SMTP/SMS credentials (see
    /// NotificationSettingsDto), so organizationId must never be trusted from the client
    /// without verifying it matches the caller's own tenant — same pattern as
    /// OrganizationsController.VerifyOrganizationOwnership.
    /// </summary>
    private ActionResult? VerifyOrganizationOwnership(Guid organizationId)
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

        if (organizationId != tenantContext.OrganizationId)
            return NotFound(new ProblemDetails
            {
                Title = "Organization not found",
                Detail = $"Organization with ID '{organizationId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// Get notifications for the current user
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<NotificationDto>>> GetNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var organizationId = _tenantAccessor.TenantContext?.OrganizationId;
        if (organizationId == null)
            return Unauthorized();

        var notifications = await _notificationService.GetUserNotificationsAsync(userId.Value, organizationId.Value, unreadOnly, limit, cancellationToken);
        var result = notifications.Select(n => new NotificationDto
        {
            Id = n.Id,
            Title = n.Title,
            Message = n.Message,
            Type = n.Type.ToString(),
            Priority = n.Priority.ToString(),
            IconClass = n.IconClass,
            ActionUrl = n.ActionUrl,
            IsRead = n.IsRead,
            CreatedAt = n.CreatedAt,
            ReadAt = n.ReadAt
        });

        return Ok(result);
    }

    /// <summary>
    /// Get unread notification count
    /// </summary>
    [HttpGet("count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var organizationId = _tenantAccessor.TenantContext?.OrganizationId;
        if (organizationId == null)
            return Unauthorized();

        var count = await _notificationService.GetUnreadCountAsync(userId.Value, organizationId.Value, cancellationToken);
        return Ok(count);
    }

    /// <summary>
    /// Mark a notification as read
    /// </summary>
    [HttpPost("{id}/read")]
    public async Task<ActionResult> MarkAsRead(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var organizationId = _tenantAccessor.TenantContext?.OrganizationId;
        if (organizationId == null)
            return Unauthorized();

        var found = await _notificationService.MarkAsReadAsync(id, userId.Value, organizationId.Value, cancellationToken);
        if (!found)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Mark all notifications as read
    /// </summary>
    [HttpPost("read-all")]
    public async Task<ActionResult> MarkAllAsRead(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var organizationId = _tenantAccessor.TenantContext?.OrganizationId;
        if (organizationId == null)
            return Unauthorized();

        await _notificationService.MarkAllAsReadAsync(userId.Value, organizationId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Delete a notification
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteNotification(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var organizationId = _tenantAccessor.TenantContext?.OrganizationId;
        if (organizationId == null)
            return Unauthorized();

        var found = await _notificationService.DeleteNotificationAsync(id, userId.Value, organizationId.Value, cancellationToken);
        if (!found)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Create a notification (admin only)
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<NotificationDto>> CreateNotification(
        [FromBody] CreateNotificationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // SECURITY: never trust client-supplied OrganizationId/UserId/BranchId here — this
        // selects which org's SMTP/SMS gateway sends the message and who receives it.
        var tenantContext = _tenantAccessor.TenantContext;
        var isSuperAdmin = RoleCodes.IsSuperAdmin(tenantContext?.UserRole);
        Guid organizationId;
        if (isSuperAdmin)
        {
            organizationId = request.OrganizationId ?? Guid.Empty;
        }
        else
        {
            if (tenantContext == null || !tenantContext.IsResolved)
                return Unauthorized(new ProblemDetails
                {
                    Title = "Tenant not resolved",
                    Detail = "Unable to determine your organization context.",
                    Status = StatusCodes.Status401Unauthorized
                });

            organizationId = tenantContext.OrganizationId;

            // User/Branch DbSets are globally filtered to the caller's own org for
            // non-SuperAdmin (see QMgrDbContext.TenantIsolationEnabled), so these checks
            // transparently reject a foreign-org target without a separate org comparison.
            if (request.UserId.HasValue && !await _dbContext.Users.AnyAsync(u => u.Id == request.UserId.Value, cancellationToken))
                return NotFound(new ProblemDetails { Title = "User not found", Status = StatusCodes.Status404NotFound });

            if (request.BranchId.HasValue && !await _dbContext.Branches.AnyAsync(b => b.Id == request.BranchId.Value && b.OrganizationId == organizationId, cancellationToken))
                return NotFound(new ProblemDetails { Title = "Branch not found", Status = StatusCodes.Status404NotFound });
        }

        var notification = await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
        {
            UserId = request.UserId,
            BranchId = request.BranchId,
            OrganizationId = organizationId,
            Title = request.Title,
            Message = request.Message,
            Type = Enum.Parse<NotificationType>(request.Type ?? "Custom"),
            Priority = Enum.Parse<NotificationPriority>(request.Priority ?? "Normal"),
            IconClass = request.IconClass,
            ActionUrl = request.ActionUrl,
            Channels = ParseChannels(request.Channels),
            PhoneNumber = request.PhoneNumber,
            Email = request.Email,
            EmailSubject = request.EmailSubject
        }, cancellationToken);

        return CreatedAtAction(nameof(GetNotifications), new NotificationDto
        {
            Id = notification.Id,
            Title = notification.Title,
            Message = notification.Message,
            Type = notification.Type.ToString(),
            Priority = notification.Priority.ToString(),
            IconClass = notification.IconClass,
            ActionUrl = notification.ActionUrl,
            IsRead = notification.IsRead,
            CreatedAt = notification.CreatedAt,
            ReadAt = notification.ReadAt
        });
    }

    #region Settings Endpoints

    /// <summary>
    /// Get notification settings for an organization
    /// </summary>
    [HttpGet("settings/{organizationId}")]
    [RequirePermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<NotificationSettingsDto>> GetSettings(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var settings = await _settingsService.GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null)
            return NotFound("Notification settings not found for this organization");

        return Ok(MapToSettingsDto(settings));
    }

    /// <summary>
    /// Create or update notification settings
    /// </summary>
    [HttpPut("settings")]
    [RequirePermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<NotificationSettingsDto>> SaveSettings(
        [FromBody] NotificationSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        var ownershipError = VerifyOrganizationOwnership(dto.OrganizationId);
        if (ownershipError != null) return ownershipError;

        var settings = new NotificationSettings
        {
            OrganizationId = dto.OrganizationId,
            SmsEnabled = dto.SmsEnabled,
            SmsGatewayUrl = dto.SmsGatewayUrl,
            SmsApiKey = dto.SmsApiKey,
            SmsUsername = dto.SmsUsername,
            SmsPassword = dto.SmsPassword,
            SmsSenderId = dto.SmsSenderId,
            SmsCustomerId = dto.SmsCustomerId,
            SmsLeadTokens = dto.SmsLeadTokens,
            EmailEnabled = dto.EmailEnabled,
            SmtpHost = dto.SmtpHost,
            SmtpPort = dto.SmtpPort,
            SmtpUseSsl = dto.SmtpUseSsl,
            SmtpUsername = dto.SmtpUsername,
            SmtpPassword = dto.SmtpPassword,
            EmailFromAddress = dto.EmailFromAddress,
            EmailFromName = dto.EmailFromName,
            TelegramEnabled = dto.TelegramEnabled,
            TelegramBotToken = dto.TelegramBotToken,
            WhatsAppEnabled = dto.WhatsAppEnabled,
            WhatsAppPhoneNumberId = dto.WhatsAppPhoneNumberId,
            WhatsAppAccessToken = dto.WhatsAppAccessToken,
            InAppEnabled = dto.InAppEnabled,
            InAppPlaySound = dto.InAppPlaySound,
            InAppRetentionDays = dto.InAppRetentionDays,
            PushEnabled = dto.PushEnabled,
            SmsTokenCreatedTemplate = dto.SmsTokenCreatedTemplate,
            SmsTokenCalledTemplate = dto.SmsTokenCalledTemplate,
            SmsReminderTemplate = dto.SmsReminderTemplate,
            EmailTokenCreatedSubject = dto.EmailTokenCreatedSubject,
            EmailTokenCreatedTemplate = dto.EmailTokenCreatedTemplate,
            EmailTokenCalledSubject = dto.EmailTokenCalledSubject,
            EmailTokenCalledTemplate = dto.EmailTokenCalledTemplate,
            UpdatedBy = GetCurrentUserId()
        };

        var saved = await _settingsService.CreateOrUpdateSettingsAsync(settings, cancellationToken);
        return Ok(MapToSettingsDto(saved));
    }

    /// <summary>
    /// Test SMS connection
    /// </summary>
    [HttpPost("settings/{organizationId}/test-sms")]
    [RequirePermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<TestResultDto>> TestSms(
        Guid organizationId,
        [FromBody] TestSmsRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var success = await _settingsService.TestSmsConnectionAsync(organizationId, request.PhoneNumber, cancellationToken);
        return Ok(new TestResultDto
        {
            Success = success,
            Message = success ? "SMS test sent successfully!" : "SMS test failed. Check your settings and try again."
        });
    }

    /// <summary>
    /// Test Email connection
    /// </summary>
    [HttpPost("settings/{organizationId}/test-email")]
    [RequirePermission(Permissions.NotificationsManage)]
    public async Task<ActionResult<TestResultDto>> TestEmail(
        Guid organizationId,
        [FromBody] TestEmailRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownershipError = VerifyOrganizationOwnership(organizationId);
        if (ownershipError != null) return ownershipError;

        var success = await _settingsService.TestEmailConnectionAsync(organizationId, request.EmailAddress, cancellationToken);
        return Ok(new TestResultDto
        {
            Success = success,
            Message = success ? "Email test sent successfully!" : "Email test failed. Check your SMTP settings and try again."
        });
    }

    #endregion

    #region Helpers

    private Guid? GetCurrentUserId()
    {
        // BUG FIX: the default JWT inbound-claim mapping renames the token's "sub" claim to
        // ClaimTypes.NameIdentifier before it reaches ClaimsPrincipal, so a literal "sub"
        // lookup never matches a real token issued by AuthController — this previously made
        // GetNotifications/GetUnreadCount/MarkAsRead/MarkAllAsRead/DeleteNotification 401 for
        // every real user. ClaimTypes.NameIdentifier is the convention used everywhere else
        // in this app (ProfileController, PermissionAuthorizationHandler, etc.).
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst("userId")?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static NotificationChannel ParseChannels(string[]? channels)
    {
        if (channels == null || channels.Length == 0)
            return NotificationChannel.InApp;

        var result = NotificationChannel.None;
        foreach (var channel in channels)
        {
            if (Enum.TryParse<NotificationChannel>(channel, true, out var ch))
                result |= ch;
        }
        return result == NotificationChannel.None ? NotificationChannel.InApp : result;
    }

    private static NotificationSettingsDto MapToSettingsDto(NotificationSettings settings) => new()
    {
        Id = settings.Id,
        OrganizationId = settings.OrganizationId,
        SmsEnabled = settings.SmsEnabled,
        SmsGatewayUrl = settings.SmsGatewayUrl,
        SmsApiKey = settings.SmsApiKey,
        SmsUsername = settings.SmsUsername,
        SmsPassword = settings.SmsPassword,
        SmsSenderId = settings.SmsSenderId,
        SmsCustomerId = settings.SmsCustomerId,
        SmsLeadTokens = settings.SmsLeadTokens,
        EmailEnabled = settings.EmailEnabled,
        SmtpHost = settings.SmtpHost,
        SmtpPort = settings.SmtpPort,
        SmtpUseSsl = settings.SmtpUseSsl,
        SmtpUsername = settings.SmtpUsername,
        SmtpPassword = settings.SmtpPassword,
        EmailFromAddress = settings.EmailFromAddress,
        EmailFromName = settings.EmailFromName,
        TelegramEnabled = settings.TelegramEnabled,
        TelegramBotToken = settings.TelegramBotToken,
        WhatsAppEnabled = settings.WhatsAppEnabled,
        WhatsAppPhoneNumberId = settings.WhatsAppPhoneNumberId,
        WhatsAppAccessToken = settings.WhatsAppAccessToken,
        InAppEnabled = settings.InAppEnabled,
        InAppPlaySound = settings.InAppPlaySound,
        InAppRetentionDays = settings.InAppRetentionDays,
        PushEnabled = settings.PushEnabled,
        SmsTokenCreatedTemplate = settings.SmsTokenCreatedTemplate,
        SmsTokenCalledTemplate = settings.SmsTokenCalledTemplate,
        SmsReminderTemplate = settings.SmsReminderTemplate,
        EmailTokenCreatedSubject = settings.EmailTokenCreatedSubject,
        EmailTokenCreatedTemplate = settings.EmailTokenCreatedTemplate,
        EmailTokenCalledSubject = settings.EmailTokenCalledSubject,
        EmailTokenCalledTemplate = settings.EmailTokenCalledTemplate
    };

    #endregion
}

#region DTOs

public class CreateNotificationRequestDto
{
    public Guid? UserId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? OrganizationId { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? Type { get; set; }
    public string? Priority { get; set; }
    public string? IconClass { get; set; }
    public string? ActionUrl { get; set; }
    public string[]? Channels { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? EmailSubject { get; set; }
}

public class NotificationSettingsDto
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    // SMS
    public bool SmsEnabled { get; set; }
    public string? SmsGatewayUrl { get; set; }
    public string? SmsApiKey { get; set; }
    public string? SmsUsername { get; set; }
    public string? SmsPassword { get; set; }
    public string? SmsSenderId { get; set; }
    public string? SmsCustomerId { get; set; }
    public int SmsLeadTokens { get; set; } = 3;

    // Email
    public bool EmailEnabled { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? EmailFromAddress { get; set; }
    public string? EmailFromName { get; set; }

    // Telegram
    public bool TelegramEnabled { get; set; }
    public string? TelegramBotToken { get; set; }

    // WhatsApp
    public bool WhatsAppEnabled { get; set; }
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppAccessToken { get; set; }

    // In-App
    public bool InAppEnabled { get; set; } = true;
    public bool InAppPlaySound { get; set; } = true;
    public int InAppRetentionDays { get; set; } = 30;

    // Push
    public bool PushEnabled { get; set; }

    // Templates
    public string? SmsTokenCreatedTemplate { get; set; }
    public string? SmsTokenCalledTemplate { get; set; }
    public string? SmsReminderTemplate { get; set; }
    public string? EmailTokenCreatedSubject { get; set; }
    public string? EmailTokenCreatedTemplate { get; set; }
    public string? EmailTokenCalledSubject { get; set; }
    public string? EmailTokenCalledTemplate { get; set; }
}

public class TestSmsRequest
{
    public required string PhoneNumber { get; set; }
}

public class TestEmailRequest
{
    public required string EmailAddress { get; set; }
}

public class TestResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

#endregion
