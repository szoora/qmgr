using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// What a batch does to each selected record. Two verbs, deliberately — every bulk edit anyone has
/// asked for is one of these two shapes, and naming them is what stops the UI becoming a drawer of
/// unrelated buttons.
/// </summary>
public enum BatchOperation
{
    /// <summary>
    /// Move each record one step along a list that has an order. Only Class qualifies on a student
    /// today, because only Class has a maintained SortOrder in the branch vocabulary.
    /// </summary>
    AdvanceClass = 0,

    /// <summary>Write one chosen value to every selected record — house, dormitory, fees status, and so on.</summary>
    SetField = 1,

    /// <summary>Retire records from the active roster. Its own operation, never a side effect of promotion.</summary>
    Deactivate = 2,

    /// <summary>Record (or withdraw) data-processing consent across a selection.</summary>
    SetConsent = 3,

    /// <summary>Give every selected welfare record the same owner for its open action.</summary>
    AssignWelfareAction = 4,

    /// <summary>Set a review date on every selected welfare record.</summary>
    SetWelfareReviewDate = 5,

    /// <summary>Move every selected welfare record to one status.</summary>
    SetWelfareStatus = 6,

    /// <summary>Check out every selected visitor who is still on site.</summary>
    CheckOutVisitors = 7,

    /// <summary>Cancel every selected waiting token — the close-of-day sweep.</summary>
    CancelTokens = 8,

    /// <summary>Cancel every selected appointment.</summary>
    CancelAppointments = 9,

    /// <summary>Activate or deactivate staff accounts.</summary>
    SetUserActive = 10,

    /// <summary>Move staff accounts onto one role.</summary>
    SetUserRole = 11
}

/// <summary>
/// Which field a <see cref="BatchOperation.SetField"/> writes. A closed list rather than a field
/// name string: an open string here would be a mass-assignment hole straight into the students
/// table, letting any caller name a column the UI never intended to expose.
/// </summary>
public enum BatchField
{
    ClassName = 0,
    House = 1,
    DormitoryOrStream = 2,
    Residency = 3,
    FeesStatus = 4,
    TransportMode = 5,
    SponsorName = 6
}

/// <summary>
/// One batch, as the operator described it.
/// </summary>
/// <remarks>
/// The same request shape runs a preview and a commit; only the endpoint differs. That is
/// deliberate — a preview that took a different payload could disagree with what the commit
/// actually did, which would make the preview worse than useless.
/// </remarks>
public record BatchRequest
{
    public BatchOperation Operation { get; init; }

    /// <summary>The ids the operator selected. Never "everything matching a filter" — a batch acts on
    /// a set somebody looked at, so the count on the confirm screen is the count that changes.</summary>
    public List<Guid> Ids { get; init; } = new();

    /// <summary>Which field, for <see cref="BatchOperation.SetField"/>.</summary>
    public BatchField? Field { get; init; }

    /// <summary>The value to write. Parsed against the field's real type server-side, never trusted.</summary>
    public string? Value { get; init; }

    /// <summary>For the welfare and appointment operations that need a date.</summary>
    public DateTime? DateValue { get; init; }

    /// <summary>For assign-to-staff and set-role.</summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>Recorded on the job and shown in its history. Optional, and worth encouraging.</summary>
    public string? Reason { get; init; }
}

/// <summary>What one record would do, or did.</summary>
public record BatchRowPreviewDto
{
    public Guid Id { get; init; }

    /// <summary>Who this row is about, in words — a student's name, a ticket number, a staff member.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Secondary identifier: a student code, a class, a counter.</summary>
    public string? SubLabel { get; init; }

    public RosterImportRowOutcome Outcome { get; init; }

    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }

    /// <summary>Why this row is being skipped, when it is. Always worth saying out loud.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// The dry run. Returns exactly the outcomes the real run will produce, computed by the same code.
/// </summary>
/// <remarks>
/// This is a first-class step rather than a confirmation dialog. Against a school roster the
/// operator needs to see "731 promoted, 88 leavers, 4 unknown class" <em>before</em> anything is
/// written — and then go and fix the four.
/// </remarks>
public record BatchPreviewDto
{
    public BatchOperation Operation { get; init; }

    /// <summary>A sentence describing the whole batch, written server-side so the confirm screen
    /// and the job history say the same thing.</summary>
    public string Summary { get; init; } = string.Empty;

    public int WillChange { get; init; }
    public int WillSkip { get; init; }
    public int WillFail { get; init; }

    public List<BatchRowPreviewDto> Rows { get; init; } = new();

    /// <summary>
    /// Set when the operation cannot run at all — no class vocabulary configured, a value that is
    /// not a valid option, nothing selected. The commit button stays disabled and this is why.
    /// </summary>
    public string? BlockingError { get; init; }
}

/// <summary>Accepted, and running. The job id is what the progress channel and the undo both use.</summary>
public record BatchAcceptedDto
{
    public Guid JobId { get; init; }
    public int TotalRows { get; init; }
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// The result of reversing a batch.
/// </summary>
/// <remarks>
/// Undo is refused outright when any record in the batch has changed since — a partial undo is
/// worse than none, because it leaves a roster in a state nobody chose and nobody can describe.
/// <see cref="ConflictedRows"/> names the records that moved so the operator can look at them.
/// </remarks>
public record BatchUndoResultDto
{
    public bool Success { get; init; }
    public int RevertedRows { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> ConflictedRows { get; init; } = new();
}
