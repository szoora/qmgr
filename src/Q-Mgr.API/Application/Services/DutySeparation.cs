namespace QMgr.API.Application.Services;

/// <summary>
/// SEGREGATION OF DUTIES HAS ONE HOME (2026-09-26, plan LESSON_PLANS_AND_SCHEMES_OF_WORK §3; NIST SP 800-53 AC-5).
///
/// Until this class, "the author cannot decide" was written by hand in seven places and missing in seven more: an
/// appraisal could be reviewed, moderated and signed by one person — its own subject included — a head of department
/// could award themselves points, the person who wrote the minutes could adopt them. Every decision on somebody's work
/// now asks this class, and the page is told the same answer through a CanI… flag computed on the server, so a button
/// the server would refuse is never drawn.
///
/// Two rules, and nothing else:
/// <list type="bullet">
/// <item><b>Nobody decides on their own work or about themselves.</b> The "author" is whoever the work is by or about.</item>
/// <item><b>Nobody takes two stages of one item.</b> Whoever acted at an earlier stage cannot also act at a later one,
/// even when they hold the later stage's permission.</item>
/// </list>
/// A stage with nobody else to take it is skipped by the caller's own rule and recorded as skipped; it is never handed
/// to the author. The answer is null when the act is allowed, otherwise the sentence a person reads.
/// </summary>
public static class DutySeparation
{
    /// <summary>Null = allowed. <paramref name="act"/> finishes "You cannot …" ("approve your own lesson plan").</summary>
    public static string? Refusal(Guid actorId, Guid? authorId, string act, params Guid?[] earlierActors)
    {
        if (actorId == Guid.Empty) return "Sign in again.";
        if (authorId.HasValue && authorId.Value == actorId)
            return $"You cannot {act}. Somebody else has to.";
        foreach (var earlier in earlierActors)
            if (earlier.HasValue && earlier.Value == actorId)
                return $"You cannot {act}: you acted at an earlier stage, so somebody else takes this one.";
        return null;
    }

    /// <summary>The flag form of <see cref="Refusal"/>, for a DTO's CanI… field.</summary>
    public static bool Allows(Guid actorId, Guid? authorId, params Guid?[] earlierActors)
        => Refusal(actorId, authorId, "act", earlierActors) == null;

    /// <summary>G1: an appraisal is moderated and signed by neither its subject nor whoever wrote the review.</summary>
    public static string? AppraisalSignOff(QMgr.Domain.Entities.Staff.StaffAppraisal a, Guid actorId, string act)
        => Refusal(actorId, a.SubjectUserId, act, a.ReviewedByUserId ?? (a.AppraiserSubmittedAt.HasValue ? a.AppraiserUserId : null));

    /// <summary>A refusal as the 403 every caller returns. The person may see the item; they may not take this step.</summary>
    public static Microsoft.AspNetCore.Mvc.ObjectResult Problem(string refusal) => new(new Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        Title = "Somebody else has to do this",
        Detail = refusal,
        Status = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden
    }) { StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden };
}
