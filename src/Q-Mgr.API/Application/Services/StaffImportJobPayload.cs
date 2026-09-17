using QMgr.Application.DTOs;

namespace QMgr.API.Application.Services;

/// <summary>
/// What a staff import job carries in <c>RosterImportJob.RowsJson</c>: the request, plus — for rows
/// delivered by slips or SMS — each row's temporary password <b>encrypted</b> with a time-limited
/// data-protection key (duty rota plan §12.2). The worker decrypts, hashes and sets it, then rewrites the
/// payload without them, so the only readable copy of a temporary password is the one returned to the
/// administrator once, when the import started. Server-side only; never serialized to a client.
/// </summary>
public record StaffImportJobPayload : StartStaffImportRequest
{
    /// <summary>Zero-based row index → protected temporary password. Emptied when the job finishes.</summary>
    public Dictionary<int, string> ProtectedTemporaryPasswords { get; set; } = new();

    /// <summary>The resolved delivery per row (zero-based), so the worker never re-derives it differently.</summary>
    public Dictionary<int, StaffImportDeliveryMode?> ResolvedDelivery { get; set; } = new();
}
