using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// Writes class-teacher (and, from plan §5, subject-teacher) assignments from the compact text forms the
/// staff import and the join-request approval accept (duty rota plan §12.4–12.5): <c>ClassTeacherOf</c>
/// ("S2A" or "S2A;S2B"). One writer, so the import and the approval apply exactly the rules the
/// class-teachers page applies: the class must exist in the branch vocabulary, is stored as the
/// vocabulary spells it, and a class already holding a class teacher is reported, never overwritten.
/// </summary>
public static class TeachingAssignments
{
    /// <summary>The outcome of one subject-teacher assignment: the row, or why not (and whether that is a conflict).</summary>
    public sealed record SubjectAssignResult(ClassTeacherAssignment? Assignment, string Title, string? Detail, bool IsConflict)
    {
        public static SubjectAssignResult Ok(ClassTeacherAssignment a) => new(a, "", null, false);
        public static SubjectAssignResult Bad(string title, string? detail = null) => new(null, title, detail, false);
        public static SubjectAssignResult Conflict(string title, string? detail = null) => new(null, title, detail, true);
    }

    /// <summary>
    /// The ONE writer of a subject-teacher assignment (duty rota plan §5.2), used by the class-teachers page, the
    /// staff import and join-request approval. The class must be in the branch vocabulary (stored as it spells
    /// it), the subject live in the organization, the person active in the organization; the same person, subject
    /// and class twice is a conflict — checked here and enforced by <c>ux_subject_teacher_once_per_class_subject</c>.
    /// Saves itself.
    /// </summary>
    public static async Task<SubjectAssignResult> AssignSubjectTeacherAsync(QMgrDbContext db, Guid organizationId, Guid branchId,
        Guid userId, Guid subjectId, string? className, int? periodsPerWeek, Guid actorUserId, CancellationToken ct = default)
    {
        if (periodsPerWeek is < 0 or > 60) return SubjectAssignResult.Bad("Periods a week must be between 0 and 60");

        var settingsJson = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);
        var match = StudentsController.ReadVocabularies(settingsJson).Classes
            .FirstOrDefault(c => ClassTeachersController.NormalizeClassName(c.Name) == ClassTeachersController.NormalizeClassName(className));
        if (match == null)
            return SubjectAssignResult.Bad("Class not found", $"\"{className?.Trim()}\" is not one of this branch's classes. Add it under Classes, houses & lists first.");

        var subject = await db.Subjects.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subjectId && s.OrganizationId == organizationId && s.IsActive, ct);
        if (subject == null) return SubjectAssignResult.Bad("Subject not found", "The subject does not exist in this organization, or has been retired.");

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.IsActive, ct);
        if (user == null) return SubjectAssignResult.Bad("Member of staff not found", "The selected user does not exist in this organization, or is no longer active.");

        var key = ClassTeachersController.NormalizeClassName(match.Name);
        if (await db.ClassTeacherAssignments.IgnoreQueryFilters().AnyAsync(a => a.BranchId == branchId && a.EndedAt == null
                && a.Role == ClassTeacherRole.SubjectTeacher && a.UserId == userId && a.SubjectId == subjectId && a.ClassName.Trim().ToLower() == key, ct))
            return SubjectAssignResult.Conflict("Already assigned", $"{user.FirstName} {user.LastName} already teaches {subject.Name} in {match.Name}.");

        var assignment = new ClassTeacherAssignment
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            ClassName = match.Name,
            UserId = userId,
            Role = ClassTeacherRole.SubjectTeacher,
            SubjectId = subjectId,
            PeriodsPerWeek = periodsPerWeek,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = actorUserId,
            CreatedBy = actorUserId
        };
        db.ClassTeacherAssignments.Add(assignment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(assignment).State = EntityState.Detached;
            return SubjectAssignResult.Conflict("Already assigned", $"{user.FirstName} {user.LastName} already teaches {subject.Name} in {match.Name}.");
        }
        return SubjectAssignResult.Ok(assignment);
    }

    /// <summary>
    /// Subject-teacher assignments from the import's <c>Teaches</c> text, "MATH:S2A,S2B; PHY:S3A" — subject CODE,
    /// a colon, classes. Groups are split on ';' or '|', classes on ','. Returns one message per thing not applied;
    /// an existing identical assignment is not a failure.
    /// </summary>
    public static async Task<List<string>> ApplyTeachesAsync(QMgrDbContext db, Guid organizationId, Guid branchId, Guid userId,
        string? teaches, Guid actorUserId, CancellationToken ct = default)
    {
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(teaches)) return messages;

        await SubjectDefaults.SeedIfEmptyAsync(db, organizationId, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ct);

        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.IsActive)
            .Select(s => new { s.Id, s.Code, s.Name })
            .ToListAsync(ct);

        foreach (var group in teaches.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = group.IndexOf(':');
            if (colon <= 0)
            {
                messages.Add($"\"{group}\" is not in the form CODE:CLASS,CLASS — not applied.");
                continue;
            }
            var code = group[..colon].Trim();
            var subject = subjects.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))
                          ?? subjects.FirstOrDefault(s => string.Equals(s.Name, code, StringComparison.OrdinalIgnoreCase));
            if (subject == null)
            {
                messages.Add($"Subject \"{code}\" is not in the subject catalogue — not applied.");
                continue;
            }
            var classes = group[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (classes.Length == 0) messages.Add($"No classes given for {subject.Code} — not applied.");
            foreach (var className in classes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var result = await AssignSubjectTeacherAsync(db, organizationId, branchId, userId, subject.Id, className, null, actorUserId, ct);
                if (result.Assignment == null && !(result.IsConflict && result.Title == "Already assigned"))
                    messages.Add($"{subject.Code} in {className}: {result.Detail ?? result.Title}");
            }
        }
        return messages;
    }

    /// <summary>"Mathematics — S2A, S2B (12 periods)": a person's live subject-teacher assignments in a branch, by subject.</summary>
    public static async Task<List<TeachingSummaryDto>> SummaryAsync(QMgrDbContext db, Guid branchId, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.BranchId == branchId && a.UserId == userId && a.EndedAt == null && a.Role == ClassTeacherRole.SubjectTeacher && a.Subject != null)
            .Select(a => new { a.SubjectId, a.Subject!.Name, a.Subject.Code, a.Subject.SortOrder, a.ClassName, a.PeriodsPerWeek })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.SubjectId!.Value)
            .Select(g => new TeachingSummaryDto
            {
                SubjectId = g.Key,
                SubjectName = g.First().Name,
                SubjectCode = g.First().Code,
                Classes = g.Select(r => r.ClassName).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
                PlannedPeriodsPerWeek = g.Sum(r => r.PeriodsPerWeek ?? 0),
                SortOrder = g.First().SortOrder
            })
            .OrderBy(t => t.SortOrder).ThenBy(t => t.SubjectName)
            .ToList();
    }

    /// <summary>Splits "S2A; S2B, S3A" into names.</summary>
    public static List<string> SplitClasses(string? text)
        => (text ?? string.Empty).Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Assigns <paramref name="userId"/> as class teacher of each named class. Returns one message per
    /// class that could not be assigned; an empty list means everything was applied. Saves itself.
    /// </summary>
    public static async Task<List<string>> ApplyClassTeacherOfAsync(QMgrDbContext db, Guid organizationId, Guid branchId, Guid userId,
        IEnumerable<string> classNames, Guid actorUserId, CancellationToken ct = default)
    {
        var messages = new List<string>();
        var names = classNames.ToList();
        if (names.Count == 0) return messages;

        var settingsJson = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync(ct);
        var vocab = StudentsController.ReadVocabularies(settingsJson).Classes;

        foreach (var name in names)
        {
            var match = vocab.FirstOrDefault(c => ClassTeachersController.NormalizeClassName(c.Name) == ClassTeachersController.NormalizeClassName(name));
            if (match == null) { messages.Add($"Class \"{name}\" is not one of this branch's classes — not assigned."); continue; }

            var key = ClassTeachersController.NormalizeClassName(match.Name);
            var live = await db.ClassTeacherAssignments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.BranchId == branchId && a.EndedAt == null && a.Role == ClassTeacherRole.ClassTeacher && a.ClassName.Trim().ToLower() == key)
                .Select(a => new { a.UserId })
                .FirstOrDefaultAsync(ct);
            if (live != null)
            {
                if (live.UserId != userId) messages.Add($"{match.Name} already has a class teacher — not assigned.");
                continue;
            }

            db.ClassTeacherAssignments.Add(new ClassTeacherAssignment
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                ClassName = match.Name,
                UserId = userId,
                Role = ClassTeacherRole.ClassTeacher,
                AssignedAt = DateTime.UtcNow,
                AssignedByUserId = actorUserId,
                CreatedBy = actorUserId
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The "one class teacher per class" partial unique index: somebody took the seat between
                // the check and the insert. Reported, not thrown.
                db.ChangeTracker.Clear();
                messages.Add($"{match.Name} already has a class teacher — not assigned.");
            }
        }
        return messages;
    }
}
