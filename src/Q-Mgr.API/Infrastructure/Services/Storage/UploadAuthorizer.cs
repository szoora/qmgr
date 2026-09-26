using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QMgr.API.Application.Services;
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
    StudentPhoto = 6,
    /// <summary>Evidence on a staff performance record. About an employee. Never public; the record's own rung and the staff scope apply.</summary>
    StaffEvidence = 7,
    /// <summary>A member of staff's own profile photograph (duty rota plan §12.3). Readable by signed-in staff of the same organization; never public.</summary>
    StaffPhoto = 8,
    /// <summary>Evidence on a duty report (plan §4.3). Readable exactly by those who may read the report.</summary>
    DutyReportAttachment = 9,
    /// <summary>
    /// An organization's own logo or favicon. PUBLIC BY INTENT — the same rung as signage media,
    /// because a kiosk, a public display and a sign-in page all fetch it with no login at all. A
    /// tenant that white-labels its sign-in page is publishing that logo by definition.
    /// </summary>
    Branding = 10,
    /// <summary>A lesson plan's or scheme of work's PDF (2026-09-26). Readable exactly by those who may read the plan —
    /// a draft's file is its author's alone.</summary>
    TeachingPlan = 11
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
    WelfareVisibility? Visibility = null,
    Guid? AuthorUserId = null,
    bool IsDraft = false)
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

    /// <summary>
    /// Forget a cached classification, after a document's shareable flag changed. Playlist membership
    /// is deliberately NOT a reason any more: since 2026-09-18 it does not enter the decision at all
    /// (see <see cref="Application.Services.MediaServing"/>), which also retired a promise this
    /// comment used to make that no playlist endpoint ever kept.
    /// </summary>
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
    private readonly IStaffScopeService _staffScope;
    private readonly IStaffPerformancePolicyService _staffPolicy;

    public UploadAuthorizer(
        QMgrDbContext db,
        IMemoryCache cache,
        ITenantContextAccessor tenantAccessor,
        IHttpContextAccessor httpContextAccessor,
        IStudentScopeService scope,
        IStaffScopeService staffScope,
        IStaffPerformancePolicyService staffPolicy)
    {
        _staffPolicy = staffPolicy;
        _staffScope = staffScope;
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

        // Short. A document that was just flagged share-only becomes gated within half a minute even
        // if nobody called Invalidate; and a public file's owner never changes, so 30 seconds saves
        // the display screens six queries per fetch.
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

        // ORDER MATTERS, and it is the most restrictive owner first. The security review found
        // the previous order (media first, matched on a client-writable FileUrl) let anyone with
        // content.create publish a welfare attachment: a URL-linked media row pointing at the
        // file's name won the classification and the file went public for ever. Now the gated
        // owners are looked up first, and media matches ONLY on FilePath — the column the server
        // writes on upload and a client can never set (CreateMediaContent also refuses a FileUrl
        // inside our own store, and MediaFilePathBackfill filled FilePath for legacy uploads).

        // 1. Welfare evidence. The record's own visibility rung and student travel with it.
        var welfare = await _db.WelfareAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FileUrl.EndsWith(suffix))
            .Select(a => new { a.Record!.OrganizationId, a.Record.BranchId, a.Record.StudentId, a.Record.Visibility })
            .FirstOrDefaultAsync(ct);
        if (welfare != null)
            return new UploadClassification(UploadOwnerKind.WelfareAttachment, IsPublic: false, welfare.OrganizationId, welfare.BranchId, welfare.StudentId, welfare.Visibility);

        // 1b. Staff evidence. The record's rung and its subject travel with it; the subject id is
        //     carried in the StudentId slot of the classification, which is "the person this file is
        //     about" for both kinds. Looked up before media for the same reason welfare is.
        var staff = await _db.StaffPerformanceAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FileUrl.EndsWith(suffix))
            .Select(a => new { a.Record!.OrganizationId, a.Record.BranchId, a.Record.SubjectUserId, a.Record.Visibility, a.Record.LoggedByUserId, IsDraft = a.Record.Status == StaffRecordStatus.Draft })
            .FirstOrDefaultAsync(ct);
        if (staff != null)
            return new UploadClassification(UploadOwnerKind.StaffEvidence, IsPublic: false, staff.OrganizationId, staff.BranchId, staff.SubjectUserId, staff.Visibility, staff.LoggedByUserId, staff.IsDraft);

        // 1c. Duty report evidence: the report's own read rule decides (StaffDutyReports.AccessForAsync).
        var dutyReport = await _db.StaffDutyReportAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FileUrl.EndsWith(suffix))
            .Select(a => new { a.Report!.OrganizationId, a.Report.BranchId, a.ReportId })
            .FirstOrDefaultAsync(ct);
        if (dutyReport != null)
            return new UploadClassification(UploadOwnerKind.DutyReportAttachment, IsPublic: false, dutyReport.OrganizationId, dutyReport.BranchId, dutyReport.ReportId);

        // 1d. A lesson plan's PDF: the plan's own read rule decides (TeachingPlans.AccessForAsync). StudentId carries the PLAN id.
        var plan = await _db.TeachingPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.FileUrl != null && p.FileUrl.EndsWith(suffix))
            .Select(p => new { p.OrganizationId, p.BranchId, p.Id })
            .FirstOrDefaultAsync(ct);
        if (plan != null)
            return new UploadClassification(UploadOwnerKind.TeachingPlan, IsPublic: false, plan.OrganizationId, plan.BranchId, plan.Id);

        // 2. Student photographs: roster-gated and row-scoped.
        var student = await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.PhotoUrl != null && s.PhotoUrl.EndsWith(suffix))
            .Select(s => new { s.OrganizationId, s.BranchId, s.Id })
            .FirstOrDefaultAsync(ct);
        if (student != null)
            return new UploadClassification(UploadOwnerKind.StudentPhoto, IsPublic: false, student.OrganizationId, student.BranchId, student.Id);

        // 3. Visitor photographs.
        var visitor = await _db.VisitorProfiles.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PhotoUrl != null && p.PhotoUrl.EndsWith(suffix))
            .Select(p => new { p.OrganizationId })
            .FirstOrDefaultAsync(ct);
        if (visitor != null)
            return new UploadClassification(UploadOwnerKind.VisitorPhoto, IsPublic: false, visitor.OrganizationId);

        // 3b. Staff profile photographs. Gated like visitor photos, looked up before media for the same reason.
        var staffPhoto = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.PhotoUrl != null && u.PhotoUrl.EndsWith(suffix))
            .Select(u => new { u.OrganizationId, u.Id })
            .FirstOrDefaultAsync(ct);
        if (staffPhoto != null)
            return new UploadClassification(UploadOwnerKind.StaffPhoto, IsPublic: false, staffPhoto.OrganizationId, null, staffPhoto.Id);

        // 4. Signage media and Library documents. Public unless share-only. FilePath only.
        // The decision is IsShareable alone — MediaServing.IsPublic is the one home for that rule.
        // It used to read `|| OnSignage` here, which made a share-only document public the moment
        // anybody added it to a playlist; the combination is now refused at the write paths instead.
        var media = await _db.MediaContents.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.FilePath == relative)
            .Select(m => new { m.OrganizationId, m.IsShareable })
            .FirstOrDefaultAsync(ct);
        if (media != null)
            return new UploadClassification(UploadOwnerKind.Media, MediaServing.IsPublic(media.IsShareable), media.OrganizationId);

        // 5. Broadcast attachments go out to contacts as links in email/Telegram/WhatsApp; the
        //    recipients have no login. Public by the sender's own choice. FilePath only, same reason.
        var broadcast = await _db.BroadcastAttachments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.FilePath == relative)
            .Select(a => new { a.Broadcast!.OrganizationId })
            .FirstOrDefaultAsync(ct);
        if (broadcast != null)
            return new UploadClassification(UploadOwnerKind.BroadcastAttachment, IsPublic: true, broadcast.OrganizationId);

        // 5b. An organization's logo or favicon. Public by intent: the sign-in page, the kiosk and
        //     every public display fetch it anonymously. Matched on the same two columns the
        //     branding endpoints write, so a link a tenant typed by hand into LogoUrl (the older
        //     free-text field, still supported) classifies the same way an uploaded one does.
        var branding = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => (o.LogoUrl != null && o.LogoUrl.EndsWith(suffix)) || (o.FaviconUrl != null && o.FaviconUrl.EndsWith(suffix)))
            .Select(o => new { o.Id })
            .FirstOrDefaultAsync(ct);
        if (branding != null)
            return new UploadClassification(UploadOwnerKind.Branding, IsPublic: true, branding.Id);

        // 6. Help-centre images: the cover column, or embedded in an article body. The help
        //    centre is readable without a login, so its images are too. Written only by a
        //    platform.docs.manage holder (DocsController), so the match on a URL is not a door.
        var docs = await _db.DocArticles.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => (d.CoverImageUrl != null && d.CoverImageUrl.EndsWith(suffix)) || d.BodyHtml.Contains(suffix), ct);
        if (docs)
            return new UploadClassification(UploadOwnerKind.DocsImage, IsPublic: true);

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

            case UploadOwnerKind.StaffPhoto:
                // A colleague's face is operational within the organization (the directory, a duty rota, a
                // report's author) and nothing more: any signed-in member of the same tenant, already checked above.
                return true;

            case UploadOwnerKind.StudentPhoto:
                if (!await HasPermissionAsync(Permissions.StudentsView, cancellationToken)) return false;
                // Any tier: the photo is on the teaching tier's list (duty rota plan §5.3) — a subject teacher
                // must recognise the children they teach. Welfare evidence below stays pastoral.
                return c.BranchId != null && c.StudentId != null && await _scope.GetTierAsync(c.BranchId.Value, c.StudentId.Value) != StudentAccessTier.None;

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

            case UploadOwnerKind.StaffEvidence:
            {
                // The subject may always read evidence about themselves at Standard and Confidential
                // (the portal is the subject-access view); Restricted stays with the rung holders
                // alone, including from the subject. Anyone else needs staff.records.view, the rung,
                // and the staff scope — the same three-part test the record itself applies.
                var callerRaw = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var isSubject = Guid.TryParse(callerRaw, out var callerId) && c.StudentId == callerId;
                // The author keeps what they wrote, below Restricted — the rule CanReadRecordAsync already
                // applies to the record itself. Without it a head of department could read the Confidential
                // observation they filed and get a 404 on its evidence (found by the plan audit, 2026-09-17).
                var isAuthor = callerId != Guid.Empty && c.AuthorUserId == callerId;
                if (isAuthor && c.Visibility != WelfareVisibility.Restricted) return true;
                // A draft is its author's alone, evidence included — the record rule again.
                if (c.IsDraft) return isAuthor && await HasPermissionAsync(Permissions.StaffRestrictedView, cancellationToken);
                if (isSubject && c.Visibility != WelfareVisibility.Restricted) return true;

                if (!await HasPermissionAsync(Permissions.StaffRecordsView, cancellationToken)) return false;
                var staffRungOk = c.Visibility switch
                {
                    WelfareVisibility.Standard => true,
                    WelfareVisibility.Confidential => await HasPermissionAsync(Permissions.StaffConfidentialView, cancellationToken),
                    WelfareVisibility.Restricted => await HasPermissionAsync(Permissions.StaffRestrictedView, cancellationToken),
                    _ => false
                };
                if (!staffRungOk) return false;
                return c.BranchId != null && c.StudentId != null && await _staffScope.CanSeeStaffAsync(c.BranchId.Value, c.StudentId.Value);
            }

            case UploadOwnerKind.DutyReportAttachment:
            {
                // StudentId carries the REPORT id for this kind. The report decides, so evidence can never be readable
                // where the report is not: a peer on the same slot gets 404 for both.
                var callerRaw = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(callerRaw, out var callerId) || c.StudentId == null || c.BranchId == null) return false;
                var report = await _db.StaffDutyReports.IgnoreQueryFilters().AsNoTracking().Include(r => r.Duty)
                    .FirstOrDefaultAsync(r => r.Id == c.StudentId.Value, cancellationToken);
                if (report?.Duty == null) return false;
                var policy = await _staffPolicy.GetAsync(report.OrganizationId);
                var access = await QMgr.API.Application.Services.StaffDutyReports.AccessForAsync(callerId, report, report.Duty, policy,
                    code => HasPermissionAsync(code, cancellationToken), _staffScope.CanSeeStaffAsync);
                return access.CanRead;
            }

            case UploadOwnerKind.TeachingPlan:
            {
                var callerRaw = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(callerRaw, out var callerId) || c.StudentId == null) return false;
                var p = await _db.TeachingPlans.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.StudentId.Value, cancellationToken);
                if (p == null) return false;
                var planPolicy = await _staffPolicy.GetAsync(p.OrganizationId);
                var chain = await QMgr.API.Application.Services.TeachingPlans.ChainAsync(_db, p.OrganizationId, p.SubjectId, cancellationToken);
                var planAccess = await QMgr.API.Application.Services.TeachingPlans.AccessForAsync(callerId, p, chain, _staffPolicy.PlanSettings(planPolicy),
                    code => HasPermissionAsync(code, cancellationToken),
                    () => _staffScope.CanSeeStaffAsync(p.BranchId, p.AuthorUserId),
                    async () => chain.DepartmentId is { } dept && await _db.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.Id == callerId && u.DepartmentIds.Contains(dept), cancellationToken));
                return planAccess.CanRead;
            }

            default:
                // Public kinds never reach here; anything new fails closed until it is classified.
                return false;
        }
    }

    /// <summary>
    /// Role AND posts, through <see cref="PostPermissionService.EffectiveCodesAsync"/> — the one home for that
    /// union. It read the role alone until 2026-09-22, so a class teacher whose derived <c>welfare.view</c> let
    /// them open a welfare record could not open its EVIDENCE with their bearer token: the record's own gate
    /// honoured the post and this one did not. The visibility rung and the class-teacher scope still apply on top.
    /// </summary>
    private async Task<bool> HasPermissionAsync(string code, CancellationToken ct)
    {
        var raw = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(raw, out var userId)) return false;

        _effectiveCodes ??= await QMgr.API.Application.Services.PostPermissionService.EffectiveCodesAsync(_db, userId, ct);
        return _effectiveCodes.Contains(code);
    }

    private HashSet<string>? _effectiveCodes;
}
