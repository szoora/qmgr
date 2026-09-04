namespace QMgr.Domain.Enums;

/// <summary>
/// Where a response sits on the graduated ladder, from acting before an incident exists through
/// to sanction. Staff reach for punishment partly because it is the only response a form makes
/// easy; grading the response is what makes the earlier rungs countable, reportable, and
/// therefore defensible when somebody asks what was tried first.
///
/// Deliberately alongside — not instead of — the existing free-text <c>ActionTaken</c>: the text
/// keeps the detail a school actually wants to read back, the stage makes it something a report
/// can group by.
/// </summary>
public enum WelfareResponseStage
{
    /// <summary>Acted before an incident existed — a flag noticed, a placement changed, a check-in arranged.</summary>
    Preventive = 0,

    /// <summary>The conversation, the apology, the mediation. Cheap, fast, and the rung most often skipped.</summary>
    Restorative = 1,

    /// <summary>A named plan with an owner and a review date — mentoring, a seating change, a weekly check-in.</summary>
    Corrective = 2,

    /// <summary>Sanction, suspension, exclusion. Legitimate and sometimes necessary.</summary>
    Punitive = 3,

    /// <summary>Passed to someone outside the school — police, probation, a health service, a district officer.</summary>
    Referral = 4
}

/// <summary>
/// Staff-perceived function of a behaviour, borrowed from functional behaviour assessment. Never
/// authoritative and never shown as a diagnosis — its whole job is to make prevention discussable
/// by asking what the child got out of it rather than only what they did.
/// </summary>
public enum WelfarePerceivedFunction
{
    Unclear = 0,

    /// <summary>Getting out of something — a lesson, a task, a place, a person.</summary>
    Escape = 1,

    /// <summary>Attention from adults or peers, including negative attention.</summary>
    Attention = 2,

    /// <summary>Access to a thing or an activity.</summary>
    Tangible = 3,

    /// <summary>Sensory regulation — noise, movement, hunger, exhaustion, pain.</summary>
    Sensory = 4
}
