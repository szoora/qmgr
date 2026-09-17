using System.Text.Json;
using QMgr.Application.Tenant;
using QMgr.Domain.Entities.Audit;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Writes <see cref="ActivityEvent"/> rows. Called EXPLICITLY from every write in the Staff
/// Performance module and from sign-in / sign-out; deliberately not an EF interceptor, which would
/// copy confidential text into a second table by default.
///
/// The actor, organization and branch come from the request; the browser's address and agent
/// come from the X-Viewer-Ip / X-Viewer-Agent headers the Web relays on every authenticated call
/// (see ViewerRequestContext on the Web side) — without them every row would name the Web
/// server's loopback. Both are stored in the truncated / coarsened form DocumentShareService
/// already defines, so there is one rule for what an address in a log looks like.
///
/// NEVER THROWS. A log row that could not be written is logged as an error; it must not fail the
/// request whose side effect it is.
/// </summary>
public interface IActivityLogger
{
    Task RecordAsync(
        string action,
        string entityType,
        Guid? entityId,
        Guid? subjectUserId,
        string summary,
        object? detail = null,
        Guid? branchId = null,
        Guid? organizationId = null,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default,
        WelfareVisibility visibility = WelfareVisibility.Standard);
}

public class ActivityLogger : IActivityLogger
{
    private readonly QMgrDbContext _db;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ActivityLogger> _logger;

    public ActivityLogger(
        QMgrDbContext db,
        ITenantContextAccessor tenantAccessor,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ActivityLogger> logger)
    {
        _db = db;
        _tenantAccessor = tenantAccessor;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task RecordAsync(
        string action, string entityType, Guid? entityId, Guid? subjectUserId, string summary,
        object? detail = null, Guid? branchId = null, Guid? organizationId = null, Guid? actorUserId = null,
        CancellationToken cancellationToken = default,
        WelfareVisibility visibility = WelfareVisibility.Standard)
    {
        try
        {
            var http = _httpContextAccessor.HttpContext;
            var tenant = _tenantAccessor.TenantContext;

            var orgId = organizationId ?? tenant?.OrganizationId ?? Guid.Empty;
            if (orgId == Guid.Empty)
            {
                _logger.LogDebug("Activity {Action} not recorded: no organization on the call", action);
                return;
            }

            var actor = actorUserId;
            if (actor == null)
            {
                var raw = http?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(raw, out var uid)) actor = uid;
            }

            // The relayed browser address wins over the connection's, which in production is the
            // Web server. Both are reduced before storing.
            string? ip = http?.Request.Headers["X-Viewer-Ip"].FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                var forwarded = http?.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
                ip = string.IsNullOrWhiteSpace(forwarded) ? http?.Connection.RemoteIpAddress?.ToString() : forwarded;
            }
            string? agent = http?.Request.Headers["X-Viewer-Agent"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(agent)) agent = http?.Request.Headers.UserAgent.ToString();

            _db.ActivityEvents.Add(new ActivityEvent
            {
                OrganizationId = orgId,
                BranchId = branchId ?? tenant?.BranchId,
                ActorUserId = actor,
                SubjectUserId = subjectUserId,
                Action = action.Length > 80 ? action[..80] : action,
                EntityType = entityType.Length > 60 ? entityType[..60] : entityType,
                EntityId = entityId,
                Summary = summary.Length > 500 ? summary[..497] + "…" : summary,
                DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
                Visibility = visibility,
                IpAddress = DocumentShareService.TruncateIp(ip),
                UserAgent = DocumentShareService.CoarseUserAgent(agent),
                OccurredAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Activity event {Action} on {EntityType} {EntityId} could not be written", action, entityType, entityId);
        }
    }
}
