using Microsoft.EntityFrameworkCore;
using QMgr.Application.Interfaces.Billing;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// AN ORGANISATION'S STORAGE IS EVERY FILE IT HAS UPLOADED (2026-09-26, lesson plans plan §5.5). Until this class only
/// the Library was counted — <c>ContentController.RecalculateStorageUsageAsync</c> summed MediaContents — so welfare
/// evidence, staff evidence, duty-report files and broadcast attachments were free, and the module's MaxStorageMb was a
/// promise about one kind of file. Every upload path that checks the quota now recomputes through here, and the sum is
/// absolute (UpdateStorageUsageAsync SETS the figure), so a missed call is corrected by the next one rather than drifting.
///
/// A new kind of upload adds one line here, the same way it adds one to <c>UploadAuthorizer.LookUpAsync</c>.
/// </summary>
public static class StorageUsage
{
    public static async Task<long> TotalBytesAsync(QMgrDbContext db, Guid organizationId, CancellationToken ct = default)
    {
        var media = await db.MediaContents.IgnoreQueryFilters().Where(m => m.OrganizationId == organizationId)
            .SumAsync(m => (long?)m.FileSizeBytes, ct) ?? 0;
        var welfare = await db.WelfareAttachments.IgnoreQueryFilters()
            .Where(a => db.WelfareRecords.IgnoreQueryFilters().Any(r => r.Id == a.RecordId && r.OrganizationId == organizationId))
            .SumAsync(a => (long?)a.FileSizeBytes, ct) ?? 0;
        var staff = await db.StaffPerformanceAttachments.IgnoreQueryFilters()
            .Where(a => db.StaffPerformanceRecords.IgnoreQueryFilters().Any(r => r.Id == a.RecordId && r.OrganizationId == organizationId))
            .SumAsync(a => (long?)a.FileSizeBytes, ct) ?? 0;
        var dutyReports = await db.StaffDutyReportAttachments.IgnoreQueryFilters()
            .Where(a => db.StaffDutyReports.IgnoreQueryFilters().Any(r => r.Id == a.ReportId && r.OrganizationId == organizationId))
            .SumAsync(a => (long?)a.FileSizeBytes, ct) ?? 0;
        var broadcasts = await db.BroadcastAttachments.IgnoreQueryFilters()
            .Where(a => db.Broadcasts.IgnoreQueryFilters().Any(b => b.Id == a.BroadcastId && b.OrganizationId == organizationId))
            .SumAsync(a => (long?)a.FileSizeBytes, ct) ?? 0;
        var plans = await db.TeachingPlans.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId && p.FileUrl != null)
            .SumAsync(p => (long?)p.FileSizeBytes, ct) ?? 0;
        return media + welfare + staff + dutyReports + broadcasts + plans;
    }

    /// <summary>Recomputes and stores the absolute figure. Never throws: a metering failure must not fail an upload.</summary>
    public static async Task RecalculateAsync(QMgrDbContext db, IUsageTrackingService usage, Guid organizationId, ILogger? logger = null, CancellationToken ct = default)
    {
        try { await usage.UpdateStorageUsageAsync(organizationId, await TotalBytesAsync(db, organizationId, ct)); }
        catch (Exception ex) { logger?.LogWarning(ex, "Storage usage for {OrganizationId} could not be recalculated", organizationId); }
    }
}
