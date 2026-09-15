using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services.Storage;

public enum UploadOwnerKind
{
    /// <summary>On disk but referenced by no row — a photo captured and never attached, or a half-finished upload. Token only.</summary>
    Unknown = 0,
    /// <summary>media_content: signage media and Library documents.</summary>
    Media = 1,
    /// <summary>Evidence on a welfare record. About a child. Never public.</summary>
    WelfareAttachment = 2,
    /// <summary>A file attached to a marketing broadcast — sent to external recipients as a link by intent.</summary>
    BroadcastAttachment = 3,
    /// <summary>A Docs (help centre) cover or body image. The help centre is public.</summary>
    DocsImage = 4,
    VisitorPhoto = 5,
    StudentPhoto = 6
}

/// <summary>
/// What a stored upload belongs to, and whether that makes it public.
/// </summary>
public sealed record UploadClassification(
    UploadOwnerKind Kind,
    bool IsPublic,
    Guid? OrganizationId = null,
    Guid? BranchId = null,
    Guid? StudentId = null,
    WelfareVisibility? Visibility = null)
{
    public static readonly UploadClassification Orphan = new(UploadOwnerKind.Unknown, IsPublic: false);
}

/// <summary>
/// The per-file decision behind <c>UploadsController</c>: which record owns this file name, is
/// it public by intent, and may the signed-in caller (if any) read it.
///
/// ONE place, on purpose. Every upload surface in the app (media, welfare evidence, broadcast
/// attachments, docs images, visitor and student photographs) writes through the same
/// <see cref="Application.Interfaces.IMediaStorageService"/> into one folder, so the only thing
/// that distinguishes a foyer poster from a photograph of an injured child is which table points
/// at it. That lookup is here and nowhere else.
/// </summary>
public interface IUploadAuthorizer
{
    Task<UploadClassification> ClassifyAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// May the current authenticated user read a NON-public file? Applies the owning record's own
    /// rules — welfare.view plus the visibility rung plus the class-teacher row scope for evidence,
    /// students.view plus the scope for a student photo, and so on — so a permission check on the
    /// record and a fetch of its bytes can never disagree.
    /// </summary>
    Task<bool> CanCurrentUserReadAsync(UploadClassification classification, CancellationToken cancellationToken = default);

    /// <summary>Forget a cached classification, e.g. after a document's shareable flag or playlist membership changed.</summary>
    void Invalidate(string fileName);
}

public class UploadAuthorizer : IUploadAuthorizer
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly QMgrDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IStudentScopeService _scope;

    public UploadAuthorizer(
        QMgrDbContext db,
        IMemoryCache cache,
        ITenantContextAccessor tenantAccessor,
        IHttpContextAccessor httpContextAccessor,
        IStudentScopeService scope)
    {
        _db = db;
        _cache = cache;
        _tenantAccessor = tenantAccessor;
        _httpContextAccessor = httpContextAccessor;
        _scope = scope;
    }

    private static string CacheKey(string fileName) => "upload-class:" + fileName;

    public void Invalidate(string fileName) => _cache.Remove(CacheKey(fileName));

    public async Task<UploadClassification> ClassifyAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey(fileName), out UploadClassification? cached) && cached != null)
            return cached;

        var result = await LookUpAsync(fileName, cancellationToken);

        // Short. A document that was just flagged share-only, or just taken off its last playlist,
        // becomes gated within half a minute even if nobody called Invalidate; and a public file's
        // owner never changes, so 30 seconds saves the display screens six queries per fetch.
        _cache.Set(CacheKey(fileName), result, CacheFor);
        return result;
    }

    /// <summary>
    /// IgnoreQueryFilters throughout: classification must be the same answer whoever asks. The
    /// tenant comparison happens afterwards, in <see cref="CanCurrentUserReadAsync"/>.
    /// </summary>
    private async Task<UploadClassification> LookUpAsync(string fileName, CancellationToken ct)
    {
        var relative = UploadAccessService.RelativeFolder + "/" + fileName;
        var suffix = "/" + relative;

        // 1. Signage media and Library documents. Public unless share-only.
        var media = await _db.MediaContents.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.FilePath == relative || (m.FileUrl != null && m.FileUrl.EndsWith(suffix)))
            .Select(m => new { m.OrganizationId, m.IsShareable, OnSignage = m.PlaylistItems.Any() })
            .FirstOrDefaultAsync(ct);
        if (media != null)
            return new UploadClassification(UploadOwnerKind.Media, IsPublic: !media.IsShareable || media.OnSignage, media.OrganizationId);

        // 2. Welfare evidence. The record's own visibility rung and student travel with it.
        var welfare = await _db.WelfareAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FileUrl.EndsWith(suffix))
            .Select(a => new { a.Record!.OrganizationId, a.Record.BranchId, a.Record.StudentId, a.Record.Visibility })
            .FirstOrDefaultAsync(ct);
        if (welfare != null)
            return new UploadClassification(UploadOwnerKind.WelfareAttachment, IsPublic: false, welfare.OrganizationId, welfare.BranchId, welfare.StudentId, welfare.Visibility);

        // 3. Broadcast attachments go out to contacts as links in email/Telegram/WhatsApp; the
        //    recipients have no login. Public by the sender's own choice.
        var broadcast = await _db.BroadcastAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FilePath == relative || a.Url.EndsWith(suffix))
            .Select(a => new { a.Broadcast!.OrganizationId })
            .FirstOrDefaultAsync(ct);
        if (broadcast != null)
            return new UploadClassification(UploadOwnerKind.BroadcastAttachment, IsPublic: true, broadcast.OrganizationId);

        // 4. Help-centre images: the cover column, or embedded in an article body. The help
        //    centre is readable without a login, so its images are too.
        var docs = await _db.DocArticles.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => (d.CoverImageUrl != null && d.CoverImageUrl.EndsWith(suffix)) || d.BodyHtml.Contains(suffix), ct);
        if (docs)
            return new UploadClassification(UploadOwnerKind.DocsImage, IsPublic: true);

        // 5. Student photographs: roster-gated and row-scoped.
        var student = await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.PhotoUrl != null && s.PhotoUrl.EndsWith(suffix))
            .Select(s => new { s.OrganizationId, s.BranchId, s.Id })
            .FirstOrDefaultAsync(ct);
        if (student != null)
            return new UploadClassification(UploadOwnerKind.StudentPhoto, IsPublic: false, student.OrganizationId, student.BranchId, student.Id);

        // 6. Visitor photographs.
        var visitor = await _db.VisitorProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PhotoUrl != null && p.PhotoUrl.EndsWith(suffix))
            .Select(p => new { p.OrganizationId })
            .FirstOrDefaultAsync(ct);
        if (visitor != null)
            return new UploadClassification(UploadOwnerKind.VisitorPhoto, IsPublic: false, visitor.OrganizationId);

        return UploadClassification.Orphan;
    }

    public async Task<bool> CanCurrentUserReadAsync(UploadClassification c, CancellationToken cancellationToken = default)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return false;

        var tenant = _tenantAccessor.TenantContext;
        if (RoleCodes.IsSuperAdmin(tenant?.UserRole)) return true;

        // A file nothing points at has no owner to apply rules for. Token only.
        if (c.Kind == UploadOwnerKind.Unknown || c.OrganizationId == null) return false;

        if (tenant == null || !tenant.IsResolved || tenant.OrganizationId != c.OrganizationId) return false;

        switch (c.Kind)
        {
            case UploadOwnerKind.Media:
                return await HasPermissionAsync(Permissions.ContentView, cancellationToken);

            case UploadOwnerKind.VisitorPhoto:
                return await HasPermissionAsync(Permissions.VisitorsView, cancellationToken);

            case UploadOwnerKind.StudentPhoto:
                if (!await HasPermissionAsync(Permissions.StudentsView, cancellationToken)) return false;
                return c.BranchId != null && c.StudentId != null && await _scope.CanSeeStudentAsync(c.BranchId.Value, c.StudentId.Value);

            case UploadOwnerKind.WelfareAttachment:
                if (!await HasPermissionAsync(Permissions.WelfareView, cancellationToken)) return false;
                // The same three rungs WelfareController applies to the record itself. NOT a
                // ladder: holding restricted without confidential must not grant the rung below.
                var rungOk = c.Visibility switch
                {
                    WelfareVisibility.Standard => true,
                    WelfareVisibility.Confidential => await HasPermissionAsync(Permissions.WelfareConfidentialView, cancellationToken),
                    WelfareVisibility.Restricted => await HasPermissionAsync(Permissions.WelfareRestrictedView, cancellationToken),
                    _ => false
                };
                if (!rungOk) return false;
                return c.BranchId != null && c.StudentId != null && await _scope.CanSeeStudentAsync(c.BranchId.Value, c.StudentId.Value);

            default:
                // Public kinds never reach here; anything new fails closed until it is classified.
                return false;
        }
    }

    private async Task<bool> HasPermissionAsync(string code, CancellationToken ct)
    {
        var raw = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(raw, out var userId)) return false;

        return await _db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(u => u.Role.RolePermissions)
            .AnyAsync(rp => rp.Permission.Code == code, ct);
    }
}
