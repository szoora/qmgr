using System.Net.Http.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// One home for "tell the server a welfare export or publish just happened in the browser".
///
/// <para>The file itself is produced client-side — <c>QDataExport</c> writes the CSV/XLSX, and
/// <c>reportPublish.js</c> renders the A4 sheet to PDF — so the server never sees it and cannot log
/// it on its own. The page reports it here immediately afterwards, which is the same shape the Staff
/// Performance module already uses (<c>IStaffPerformanceApiService.RecordExportAsync</c>).</para>
///
/// <para>Deliberately NOT the staff endpoint. That one resolves every kind to a <c>staff.*</c>
/// permission and writes an activity row about a member of staff; the subject here is a child. The
/// welfare endpoint checks the student against <c>IStudentScopeService</c>, so a class teacher cannot
/// record a line about a child they could not have exported.</para>
///
/// <para><b>Never throws and never toasts.</b> This is a side effect of an export that has already
/// succeeded and whose file the reader already has: failing the visible action because its audit row
/// did not write would be the worst possible trade. Same reasoning as
/// <c>VisitorsController.TryIssueVisitToken</c> — a degraded success, not a failure. It is logged to
/// the browser console so it is still discoverable.</para>
/// </summary>
public static class WelfareActivityReporting
{
    /// <summary>Reports an export or publish. Returns true when the row was accepted.</summary>
    public static async Task<bool> ReportAsync(
        HttpClient http, Guid branchId, RecordWelfareExportRequest request)
    {
        if (branchId == Guid.Empty) return false;

        try
        {
            var response = await http.PostAsJsonAsync(
                $"api/v1/branches/{branchId}/welfare/activity/exports", request);
            if (response.IsSuccessStatusCode) return true;

            Console.WriteLine(
                $"[WelfareActivityReporting] {request.Kind} not recorded: {(int)response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WelfareActivityReporting] {request.Kind} not recorded: {ex.Message}");
            return false;
        }
    }
}
