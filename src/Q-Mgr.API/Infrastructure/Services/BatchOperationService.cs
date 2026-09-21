using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Works out what a batch would do, without doing any of it.
/// </summary>
/// <remarks>
/// <para>
/// The preview and the commit run <b>the same resolver</b> — this class. That is the whole point:
/// a preview computed by different code from the commit could disagree with it, which would make
/// the preview worse than useless, since an operator would have trusted it. The commit path calls
/// <see cref="ResolveAsync"/> too and simply writes what comes back.
/// </para>
/// <para>
/// Nothing here touches <c>PUT /students/{id}</c>. That endpoint is a full replace — a partial
/// payload clears the fields it omits, which is recorded in this project's task tracker as having
/// silently wiped a class during testing. A batch built on it would inherit that hazard once per
/// selected student. Every write below names exactly the one field it changes.
/// </para>
/// </remarks>
public interface IBatchOperationService
{
    Task<BatchPreviewDto> ResolveAsync(Guid branchId, BatchRequest request, CancellationToken ct = default);
}

public class BatchOperationService : IBatchOperationService
{
    private readonly QMgrDbContext _context;

    /// <summary>A batch is a set somebody selected on a list, not a filter. This is a sanity ceiling.</summary>
    public const int MaxBatchSize = 5000;

    public BatchOperationService(QMgrDbContext context)
    {
        _context = context;
    }

    public async Task<BatchPreviewDto> ResolveAsync(Guid branchId, BatchRequest request, CancellationToken ct = default)
    {
        if (request.Ids.Count == 0)
            return Blocked(request, "Nothing is selected.");

        if (request.Ids.Count > MaxBatchSize)
            return Blocked(request, $"A batch is capped at {MaxBatchSize:N0} records. Narrow the selection.");

        return request.Operation switch
        {
            BatchOperation.AdvanceClass => await ResolveAdvanceClassAsync(branchId, request, ct),
            BatchOperation.SetField => await ResolveSetFieldAsync(branchId, request, ct),
            BatchOperation.Deactivate => await ResolveDeactivateAsync(branchId, request, ct),
            BatchOperation.SetConsent => await ResolveConsentAsync(branchId, request, ct),

            BatchOperation.AssignWelfareAction => await ResolveWelfareAsync(branchId, request, ct),
            BatchOperation.SetWelfareReviewDate => await ResolveWelfareAsync(branchId, request, ct),
            BatchOperation.SetWelfareStatus => await ResolveWelfareAsync(branchId, request, ct),

            BatchOperation.CheckOutVisitors => await ResolveVisitorCheckOutAsync(branchId, request, ct),
            BatchOperation.CancelTokens => await ResolveCancelTokensAsync(branchId, request, ct),
            BatchOperation.CancelAppointments => await ResolveCancelAppointmentsAsync(branchId, request, ct),

            BatchOperation.SetUserActive => await ResolveUserActiveAsync(branchId, request, ct),
            BatchOperation.SetUserRole => await ResolveUserRoleAsync(branchId, request, ct),

            _ => Blocked(request, "That operation isn't supported.")
        };
    }

    // =============================================================================================
    // Students
    // =============================================================================================

    /// <summary>
    /// Promotion. The rule, stated exactly: find the student's current class in the branch's
    /// <em>active</em> class list ordered by SortOrder, and move them to the one after it.
    /// </summary>
    /// <remarks>
    /// Four cases, each reported rather than absorbed. A student in the final class is a leaver and
    /// is <b>not</b> deactivated here — retiring a child hides them from the roster, the gate and
    /// visiting day, and that is a decision somebody makes deliberately, never a side effect of
    /// pressing Promote. A student whose class is not in the list (free text from before the
    /// vocabulary existed, or a typo) is skipped and named, because guessing is how a child ends up
    /// in the wrong stream.
    /// </remarks>
    private async Task<BatchPreviewDto> ResolveAdvanceClassAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var settings = await _context.Branches.AsNoTracking()
            .Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);

        var classes = StudentsController.ReadVocabularies(settings).Classes
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .Select(c => c.Name)
            .ToList();

        if (classes.Count < 2)
            return Blocked(request, "This branch has fewer than two active classes, so there is nothing to promote into. Set the class list up under Lists first.");

        var students = await LoadStudentsAsync(branchId, request.Ids, ct);
        var rows = new List<BatchRowPreviewDto>(students.Count);

        foreach (var s in students)
        {
            var current = s.ClassName?.Trim();

            if (string.IsNullOrWhiteSpace(current))
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped, null, null,
                    "No class on file — set one before promoting."));
                continue;
            }

            var index = classes.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped, current, null,
                    $"\"{current}\" is not one of this branch's classes, so there is no next class to move to."));
                continue;
            }

            if (index == classes.Count - 1)
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped, current, null,
                    $"Final class — a leaver. Promotion does not retire anybody; use \"Mark as left\" if that is what you mean."));
                continue;
            }

            rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Updated, current, classes[index + 1],
                $"{current} → {classes[index + 1]}"));
        }

        AddMissing(rows, students.Select(s => s.Id), request.Ids, "That student no longer exists on this branch.");

        var promoted = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        var leavers = rows.Count(r => r.Outcome == RosterImportRowOutcome.Skipped && (r.Message.StartsWith("Final class")));

        return Build(request, rows,
            $"Promote {promoted:N0} student{(promoted == 1 ? "" : "s")} to their next class" +
            (leavers > 0 ? $" · {leavers:N0} in the final class left where they are" : ""));
    }

    private async Task<BatchPreviewDto> ResolveSetFieldAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        if (request.Field is not { } field)
            return Blocked(request, "No field was chosen.");

        var value = request.Value?.Trim();

        // Validate the value against the field's real type before anything is previewed, so an
        // operator never sees 800 green rows for a value the write would then reject.
        var (valid, error, normalized) = ValidateFieldValue(field, value);
        if (!valid) return Blocked(request, error!);

        var students = await LoadStudentsAsync(branchId, request.Ids, ct);
        var rows = new List<BatchRowPreviewDto>(students.Count);

        foreach (var s in students)
        {
            var current = ReadField(s, field);

            if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped, current, normalized,
                    "Already set to that value."));
                continue;
            }

            rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Updated, current, normalized,
                $"{(string.IsNullOrWhiteSpace(current) ? "—" : current)} → {(string.IsNullOrWhiteSpace(normalized) ? "cleared" : normalized)}"));
        }

        AddMissing(rows, students.Select(s => s.Id), request.Ids, "That student no longer exists on this branch.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows,
            $"Set {FieldLabel(field)} to \"{normalized}\" on {changing:N0} student{(changing == 1 ? "" : "s")}");
    }

    private async Task<BatchPreviewDto> ResolveDeactivateAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var students = await LoadStudentsAsync(branchId, request.Ids, ct);
        var rows = new List<BatchRowPreviewDto>(students.Count);

        foreach (var s in students)
        {
            if (!s.IsActive)
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped, "Inactive", "Inactive",
                    "Already off the active roster."));
                continue;
            }

            rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Updated, "Active", "Inactive",
                $"{s.ClassName ?? "no class"} — removed from the active roster"));
        }

        AddMissing(rows, students.Select(s => s.Id), request.Ids, "That student no longer exists on this branch.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows,
            $"Mark {changing:N0} student{(changing == 1 ? "" : "s")} as left — they stop appearing on the roster and at the gate, and their records are kept");
    }

    private async Task<BatchPreviewDto> ResolveConsentAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var giving = !string.Equals(request.Value, "withdraw", StringComparison.OrdinalIgnoreCase);
        var students = await LoadStudentsAsync(branchId, request.Ids, ct);
        var rows = new List<BatchRowPreviewDto>(students.Count);

        foreach (var s in students)
        {
            var has = s.DataConsentGivenAt.HasValue;
            if (has == giving)
            {
                rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Skipped,
                    has ? "Recorded" : "Not recorded", has ? "Recorded" : "Not recorded", "No change needed."));
                continue;
            }

            rows.Add(Row(s.Id, s.FullName, s.StudentCode, RosterImportRowOutcome.Updated,
                has ? "Recorded" : "Not recorded", giving ? "Recorded" : "Not recorded",
                giving ? "Consent recorded" : "Consent withdrawn"));
        }

        AddMissing(rows, students.Select(s => s.Id), request.Ids, "That student no longer exists on this branch.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows,
            giving
                ? $"Record data consent for {changing:N0} student{(changing == 1 ? "" : "s")}"
                : $"Withdraw data consent for {changing:N0} student{(changing == 1 ? "" : "s")}");
    }

    // =============================================================================================
    // Welfare
    // =============================================================================================

    private async Task<BatchPreviewDto> ResolveWelfareAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        if (request.Operation == BatchOperation.AssignWelfareAction && request.TargetUserId is null)
            return Blocked(request, "Choose the staff member to assign these to.");

        if (request.Operation == BatchOperation.SetWelfareReviewDate && request.DateValue is null)
            return Blocked(request, "Choose a review date.");

        WelfareStatus? status = null;
        if (request.Operation == BatchOperation.SetWelfareStatus)
        {
            if (!Enum.TryParse<WelfareStatus>(request.Value, true, out var parsed))
                return Blocked(request, "That is not a valid status.");
            if (parsed == WelfareStatus.Draft)
                return Blocked(request, "A record cannot be moved back to Draft in bulk — a draft is somebody's unfinished note.");
            status = parsed;
        }

        var records = await _context.WelfareRecords.AsNoTracking()
            .Where(r => r.BranchId == branchId && request.Ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Description, r.Status, r.AssignedToUserId, r.ActionDueDate, r.StudentId })
            .ToListAsync(ct);

        var names = await _context.Students.AsNoTracking()
            .Where(s => records.Select(r => r.StudentId).Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName, ct);

        string? targetName = null;
        if (request.TargetUserId is { } uid)
        {
            targetName = await _context.Users.AsNoTracking().Where(u => u.Id == uid)
                .Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(targetName)) return Blocked(request, "That staff member no longer exists.");
        }

        var rows = new List<BatchRowPreviewDto>(records.Count);
        foreach (var r in records)
        {
            var label = names.GetValueOrDefault(r.StudentId, "Unknown student");
            var sub = r.Description.Length > 60 ? r.Description[..60] + "…" : r.Description;

            if (r.Status == WelfareStatus.Draft)
            {
                rows.Add(Row(r.Id, label, sub, RosterImportRowOutcome.Skipped, "Draft", null,
                    "Still a draft — finalize it before it can be worked on in bulk."));
                continue;
            }

            switch (request.Operation)
            {
                case BatchOperation.AssignWelfareAction:
                    rows.Add(Row(r.Id, label, sub, RosterImportRowOutcome.Updated,
                        r.AssignedToUserId?.ToString(), request.TargetUserId?.ToString(), $"Assigned to {targetName}"));
                    break;

                case BatchOperation.SetWelfareReviewDate:
                    rows.Add(Row(r.Id, label, sub, RosterImportRowOutcome.Updated,
                        r.ActionDueDate?.ToString("yyyy-MM-dd"), request.DateValue?.ToString("yyyy-MM-dd"),
                        string.Create(CultureInfo.InvariantCulture, $"Review by {request.DateValue:d MMM yyyy}")));
                    break;

                default:
                    if (r.Status == status)
                    {
                        rows.Add(Row(r.Id, label, sub, RosterImportRowOutcome.Skipped, r.Status.ToString(), status?.ToString(),
                            "Already at that status."));
                        break;
                    }
                    rows.Add(Row(r.Id, label, sub, RosterImportRowOutcome.Updated,
                        r.Status.ToString(), status?.ToString(), $"{r.Status} → {status}"));
                    break;
            }
        }

        AddMissing(rows, records.Select(r => r.Id), request.Ids, "That record no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        var summary = request.Operation switch
        {
            BatchOperation.AssignWelfareAction => $"Assign {changing:N0} welfare record{(changing == 1 ? "" : "s")} to {targetName}",
            BatchOperation.SetWelfareReviewDate => string.Create(CultureInfo.InvariantCulture, $"Set a review date of {request.DateValue:d MMM yyyy} on {changing:N0} record{(changing == 1 ? "" : "s")}"),
            _ => $"Move {changing:N0} record{(changing == 1 ? "" : "s")} to {status}"
        };

        return Build(request, rows, summary);
    }

    // =============================================================================================
    // Visitors, queue, appointments, users
    // =============================================================================================

    private async Task<BatchPreviewDto> ResolveVisitorCheckOutAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var visitors = await _context.Visitors.AsNoTracking()
            .Where(v => v.BranchId == branchId && request.Ids.Contains(v.Id) && v.DeletedAt == null)
            .Select(v => new { v.Id, Name = v.VisitorProfile!.FullName, v.CheckedInAt, v.CheckedOutAt, v.Purpose })
            .ToListAsync(ct);

        var rows = new List<BatchRowPreviewDto>(visitors.Count);
        foreach (var v in visitors)
        {
            if (v.CheckedOutAt.HasValue)
            {
                rows.Add(Row(v.Id, v.Name, v.Purpose, RosterImportRowOutcome.Skipped,
                    "Checked out", "Checked out", $"Already left at {v.CheckedOutAt:HH:mm}."));
                continue;
            }

            if (!v.CheckedInAt.HasValue)
            {
                rows.Add(Row(v.Id, v.Name, v.Purpose, RosterImportRowOutcome.Skipped, null, null,
                    "Never checked in, so there is nothing to close."));
                continue;
            }

            rows.Add(Row(v.Id, v.Name, v.Purpose, RosterImportRowOutcome.Updated,
                $"On site since {v.CheckedInAt:HH:mm}", "Checked out", "Closed out"));
        }

        AddMissing(rows, visitors.Select(v => v.Id), request.Ids, "That visit no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows, $"Check out {changing:N0} visitor{(changing == 1 ? "" : "s")} still on site");
    }

    private async Task<BatchPreviewDto> ResolveCancelTokensAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var tokens = await _context.Tokens.AsNoTracking()
            .Where(t => t.BranchId == branchId && request.Ids.Contains(t.Id))
            .Select(t => new { t.Id, t.DisplayNumber, t.Status, t.CustomerName, t.CreatedAt })
            .ToListAsync(ct);

        var rows = new List<BatchRowPreviewDto>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.Status is not (TokenStatus.Waiting or TokenStatus.Called))
            {
                rows.Add(Row(t.Id, t.DisplayNumber, t.CustomerName, RosterImportRowOutcome.Skipped,
                    t.Status.ToString(), t.Status.ToString(), "Already finished."));
                continue;
            }

            rows.Add(Row(t.Id, t.DisplayNumber, t.CustomerName, RosterImportRowOutcome.Updated,
                t.Status.ToString(), nameof(TokenStatus.Cancelled), $"Waiting since {t.CreatedAt.ToLocalTime():HH:mm}"));
        }

        AddMissing(rows, tokens.Select(t => t.Id), request.Ids, "That ticket no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows, $"Cancel {changing:N0} ticket{(changing == 1 ? "" : "s")}");
    }

    private async Task<BatchPreviewDto> ResolveCancelAppointmentsAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var appts = await _context.Appointments.AsNoTracking()
            .Where(a => a.BranchId == branchId && request.Ids.Contains(a.Id))
            .Select(a => new { a.Id, a.CustomerName, a.ScheduledAt, a.Status, a.ReferenceCode })
            .ToListAsync(ct);

        var rows = new List<BatchRowPreviewDto>(appts.Count);
        foreach (var a in appts)
        {
            if (a.Status is AppointmentStatus.Cancelled or AppointmentStatus.Completed or AppointmentStatus.NoShow)
            {
                rows.Add(Row(a.Id, a.CustomerName ?? a.ReferenceCode, a.ReferenceCode, RosterImportRowOutcome.Skipped,
                    a.Status.ToString(), a.Status.ToString(), "Already closed."));
                continue;
            }

            rows.Add(Row(a.Id, a.CustomerName ?? a.ReferenceCode, a.ReferenceCode, RosterImportRowOutcome.Updated,
                a.Status.ToString(), nameof(AppointmentStatus.Cancelled),
                string.Create(CultureInfo.InvariantCulture, $"{a.ScheduledAt.ToLocalTime():d MMM, HH:mm} — cancelled")));
        }

        AddMissing(rows, appts.Select(a => a.Id), request.Ids, "That booking no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows, $"Cancel {changing:N0} booking{(changing == 1 ? "" : "s")}");
    }

    private async Task<BatchPreviewDto> ResolveUserActiveAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        var activating = string.Equals(request.Value, "true", StringComparison.OrdinalIgnoreCase);
        var orgId = await _context.Branches.AsNoTracking().Where(b => b.Id == branchId)
            .Select(b => b.OrganizationId).FirstOrDefaultAsync(ct);

        var users = await _context.Users.AsNoTracking()
            .Where(u => u.OrganizationId == orgId && request.Ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, Name = (u.FirstName + " " + u.LastName).Trim(), u.IsActive })
            .ToListAsync(ct);

        var rows = new List<BatchRowPreviewDto>(users.Count);
        foreach (var u in users)
        {
            if (u.IsActive == activating)
            {
                rows.Add(Row(u.Id, string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "Unknown") : u.Name, u.Email,
                    RosterImportRowOutcome.Skipped, u.IsActive ? "Active" : "Inactive", null, "No change needed."));
                continue;
            }

            rows.Add(Row(u.Id, string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "Unknown") : u.Name, u.Email,
                RosterImportRowOutcome.Updated, u.IsActive ? "Active" : "Inactive", activating ? "Active" : "Inactive",
                activating ? "Account enabled" : "Account disabled — they can no longer sign in"));
        }

        AddMissing(rows, users.Select(u => u.Id), request.Ids, "That account no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows,
            activating ? $"Enable {changing:N0} account{(changing == 1 ? "" : "s")}" : $"Disable {changing:N0} account{(changing == 1 ? "" : "s")}");
    }

    private async Task<BatchPreviewDto> ResolveUserRoleAsync(Guid branchId, BatchRequest request, CancellationToken ct)
    {
        if (request.TargetUserId is null) return Blocked(request, "Choose the role to move these accounts onto.");

        var orgId = await _context.Branches.AsNoTracking().Where(b => b.Id == branchId)
            .Select(b => b.OrganizationId).FirstOrDefaultAsync(ct);

        var role = await _context.Roles.AsNoTracking()
            .Where(r => r.Id == request.TargetUserId && (r.OrganizationId == orgId || r.OrganizationId == null) && r.IsActive)
            .Select(r => new { r.Id, r.Name, r.Code })
            .FirstOrDefaultAsync(ct);

        if (role == null) return Blocked(request, "That role isn't available to this organization.");

        // A platform role must never be handed out by a tenant's bulk action.
        if (RoleCodes.IsSuperAdmin(role.Code))
            return Blocked(request, "The platform administrator role cannot be assigned in bulk.");

        var users = await _context.Users.AsNoTracking()
            .Where(u => u.OrganizationId == orgId && request.Ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, Name = (u.FirstName + " " + u.LastName).Trim(), u.RoleId, RoleName = u.Role!.Name })
            .ToListAsync(ct);

        var rows = new List<BatchRowPreviewDto>(users.Count);
        foreach (var u in users)
        {
            if (u.RoleId == role.Id)
            {
                rows.Add(Row(u.Id, string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "Unknown") : u.Name, u.Email,
                    RosterImportRowOutcome.Skipped, u.RoleName, role.Name, "Already on that role."));
                continue;
            }

            rows.Add(Row(u.Id, string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "Unknown") : u.Name, u.Email,
                RosterImportRowOutcome.Updated, u.RoleId.ToString(), role.Id.ToString(), $"{u.RoleName} → {role.Name}"));
        }

        AddMissing(rows, users.Select(u => u.Id), request.Ids, "That account no longer exists.");

        var changing = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated);
        return Build(request, rows, $"Move {changing:N0} account{(changing == 1 ? "" : "s")} to {role.Name}");
    }

    // =============================================================================================
    // Field plumbing
    // =============================================================================================

    /// <summary>
    /// Checks a value against the field's real type before anything is previewed. An enum field
    /// gets its name parsed; a vocabulary field is left free because the branch's own list is not
    /// closed. The alternative — validating only at write time — shows an operator eight hundred
    /// green rows for a value the write then rejects.
    /// </summary>
    private static (bool Valid, string? Error, string? Normalized) ValidateFieldValue(BatchField field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Clearing is legitimate for the free-text fields and meaningless for the enums.
            return field is BatchField.Residency or BatchField.FeesStatus or BatchField.TransportMode
                ? (false, $"Choose a {FieldLabel(field).ToLowerInvariant()}.", null)
                : (true, null, null);
        }

        return field switch
        {
            BatchField.Residency => Parse<StudentResidency>(value, "residency"),
            BatchField.FeesStatus => Parse<StudentFeesStatus>(value, "fees status"),
            BatchField.TransportMode => Parse<StudentTransportMode>(value, "transport mode"),
            _ => value.Length > 100
                ? (false, $"{FieldLabel(field)} must be 100 characters or fewer.", null)
                : (true, null, value)
        };

        static (bool, string?, string?) Parse<TEnum>(string v, string label) where TEnum : struct, Enum =>
            Enum.TryParse<TEnum>(v, true, out var parsed)
                ? (true, null, parsed.ToString())
                : (false, $"\"{v}\" is not a valid {label}.", null);
    }

    private static string? ReadField(StudentSnapshot s, BatchField field) => field switch
    {
        BatchField.ClassName => s.ClassName,
        BatchField.House => s.House,
        BatchField.DormitoryOrStream => s.DormitoryOrStream,
        BatchField.Residency => s.Residency?.ToString(),
        BatchField.FeesStatus => s.FeesStatus?.ToString(),
        BatchField.TransportMode => s.TransportMode?.ToString(),
        BatchField.SponsorName => s.SponsorName,
        _ => null
    };

    internal static string FieldLabel(BatchField field) => field switch
    {
        BatchField.ClassName => "Class",
        BatchField.House => "House",
        BatchField.DormitoryOrStream => "Dormitory / stream",
        BatchField.Residency => "Residency",
        BatchField.FeesStatus => "Fees status",
        BatchField.TransportMode => "Transport",
        BatchField.SponsorName => "Sponsor",
        _ => field.ToString()
    };

    // =============================================================================================
    // Shared helpers
    // =============================================================================================

    internal record StudentSnapshot(Guid Id, string FullName, string? StudentCode, string? ClassName, string? House,
        string? DormitoryOrStream, StudentResidency? Residency, StudentFeesStatus? FeesStatus,
        StudentTransportMode? TransportMode, string? SponsorName, bool IsActive, DateTime? DataConsentGivenAt);

    private async Task<List<StudentSnapshot>> LoadStudentsAsync(Guid branchId, List<Guid> ids, CancellationToken ct) =>
        await _context.Students.AsNoTracking()
            .Where(s => s.BranchId == branchId && ids.Contains(s.Id))
            .Select(s => new StudentSnapshot(s.Id, s.FullName, s.StudentCode, s.ClassName, s.House,
                s.DormitoryOrStream, s.Residency, s.FeesStatus, s.TransportMode, s.SponsorName, s.IsActive, s.DataConsentGivenAt))
            .ToListAsync(ct);

    private static BatchRowPreviewDto Row(Guid id, string label, string? sub, RosterImportRowOutcome outcome,
        string? previous, string? next, string message) =>
        new() { Id = id, Label = label, SubLabel = sub, Outcome = outcome, PreviousValue = previous, NewValue = next, Message = message };

    /// <summary>
    /// A selected id that no longer resolves is reported, never dropped. Silently shrinking a batch
    /// from 731 to 729 is how somebody concludes the feature worked when two children were missed.
    /// </summary>
    private static void AddMissing(List<BatchRowPreviewDto> rows, IEnumerable<Guid> found, List<Guid> requested, string message)
    {
        var have = found.ToHashSet();
        foreach (var id in requested.Where(id => !have.Contains(id)))
            rows.Add(new BatchRowPreviewDto { Id = id, Label = "(not found)", Outcome = RosterImportRowOutcome.Failed, Message = message });
    }

    private static BatchPreviewDto Build(BatchRequest request, List<BatchRowPreviewDto> rows, string summary) => new()
    {
        Operation = request.Operation,
        Summary = summary,
        Rows = rows.OrderBy(r => r.Outcome == RosterImportRowOutcome.Updated ? 1 : 0).ThenBy(r => r.Label).ToList(),
        WillChange = rows.Count(r => r.Outcome == RosterImportRowOutcome.Updated),
        WillSkip = rows.Count(r => r.Outcome == RosterImportRowOutcome.Skipped),
        WillFail = rows.Count(r => r.Outcome == RosterImportRowOutcome.Failed)
    };

    private static BatchPreviewDto Blocked(BatchRequest request, string error) => new()
    {
        Operation = request.Operation,
        Summary = string.Empty,
        BlockingError = error
    };
}
