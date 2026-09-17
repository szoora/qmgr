using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Welfare;

/// <summary>
/// Links a staff user to a class they hold pastoral responsibility for. Two things depend on it:
/// who gets told when a student in that class has a case logged, and — for a role whose
/// <c>DataScope</c> is <c>AssignedClasses</c> — which students that user can see at all.
///
/// A NEW TABLE, and the project's standing "enhance an existing row before adding a table" rule
/// means the reason has to be written down. The obvious cheaper shape is a
/// <c>ClassTeacherUserId</c> field on the class vocabulary item — classes are not a table, they
/// are a JSON list in <c>Branch.Settings</c> (<c>BranchVocabulariesDto.Classes</c>). It fails four
/// ways, and the first one loses data:
///
///  - <c>StudentsController.UpdateVocabularies</c> writes <c>request.Vocabularies</c> OVER the
///    stored blob. Any client that round-trips the class list without knowing about a teacher
///    field — an older browser tab, a future editor, the bulk-import path — silently deletes every
///    assignment on Save. That is not hypothetical; it is how that endpoint is written.
///  - No foreign key. A deleted or deactivated user leaves a dangling GUID in JSON with no cascade
///    and no way to answer "who was this?".
///  - Wrong query direction. The scope filter runs on nearly every welfare read and needs "which
///    classes does user X teach?". Against JSON that means scanning and parsing every branch's
///    settings blob; against this table it is one indexed lookup on UserId.
///  - No history. "Who could see this child's record last term?" is a question a school will be
///    asked, and a blob holding only current state cannot answer it.
///
/// The class VOCABULARY still lives in <c>Branch.Settings</c> and stays the source of truth for
/// which classes exist — this table references one by name, validated on write and renamed in
/// lockstep by <c>UpdateVocabularies</c>.
///
/// Branch-scoped with no global EF query filter, exactly like <c>Student</c>,
/// <c>WelfareRecord</c> and <c>StudentFlag</c> — every controller action reaching one by ID must
/// call VerifyBranchOwnership explicitly.
/// </summary>
public class ClassTeacherAssignment : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>
    /// Matched to <c>Branch.Settings → Vocabularies.Classes</c> by name, case-insensitively.
    /// Stored as the user typed it in the vocabulary so the UI reads back correctly; every
    /// comparison is on the lowered form. See <c>ClassTeachersController.NormalizeClassName</c>.
    /// </summary>
    [MaxLength(100)]
    public string ClassName { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>
    /// One <see cref="ClassTeacherRole.ClassTeacher"/> per class (enforced by a partial unique
    /// index); any number of assistants. Both are notified and both are scoped identically — the
    /// distinction is who the roster and student profile name as "the" class teacher.
    /// </summary>
    public ClassTeacherRole Role { get; set; } = ClassTeacherRole.ClassTeacher;

    /// <summary>The subject taught, for a SubjectTeacher assignment (required then, null otherwise). Duty rota plan §5.2.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>Planned periods a week for this subject in this class — what the timetable's unplaced load and the teaching-load report compare against.</summary>
    public int? PeriodsPerWeek { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public Guid AssignedByUserId { get; set; }

    /// <summary>
    /// Null means the assignment is live. Ending one never deletes it: the history of who could
    /// see a child's record, and when, is itself the audit answer a school will be asked for.
    /// </summary>
    public DateTime? EndedAt { get; set; }
    public Guid? EndedByUserId { get; set; }

    [MaxLength(500)]
    public string? EndReason { get; set; }

    /// <summary>Convenience for queries and UI; equivalent to <c>EndedAt == null</c>.</summary>
    public bool IsLive => EndedAt == null;

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
    public virtual Identity.User? User { get; set; }
    public virtual Staff.Subject? Subject { get; set; }
}
