namespace QMgr.Domain.Enums;

// Staff self-service configuration. Values are APPENDED, never inserted — the rule the rest of this
// folder states on itself: slotting a value in the middle silently rewrites the meaning of stored rows.

/// <summary>
/// Why a teacher cannot be timetabled in a slot. A SHORT LIST RATHER THAN FREE TEXT, deliberately:
/// "hospital appointment, Thursdays" typed into a note is health information about a member of staff,
/// and once it exists it has to be retained, gated and eventually blanked like any other. A category
/// tells the timetable everything it needs and tells the reader nothing it does not.
/// </summary>
public enum UnavailabilityReason
{
    /// <summary>No reason given. The default, and a complete answer — the school need not know.</summary>
    Unspecified = 0,
    /// <summary>Contracted not to work then: part-time, a fractional post.</summary>
    ContractedHours = 1,
    /// <summary>Committed elsewhere in the school at that time.</summary>
    OtherSchoolDuty = 2,
    /// <summary>Studying, training or on a course.</summary>
    StudyOrTraining = 3,
    /// <summary>A recurring personal commitment.</summary>
    Personal = 4
}

/// <summary>What a self-service request is asking for. The queue is ONE queue; only the effect differs.</summary>
public enum ConfigRequestKind
{
    /// <summary>Place, move or release a lesson the requester would teach. Grants no new data access.</summary>
    TimetableSlot = 0,
    /// <summary>
    /// A class and subject the requester would teach. GRANTS StudentAccessTier.Teaching over that
    /// class — name, photograph, class, and the right to file a welfare record — so it can never be
    /// self-approved, whatever the requester holds.
    /// </summary>
    ClassAssignment = 1,
    /// <summary>
    /// Swap two placed lessons between two teachers. Needs the other teacher AND a decider.
    ///
    /// <see cref="StaffConfigRequest.EffectiveOn"/> decides which kind of swap it is: null is PERMANENT
    /// (applied by re-publishing the version with the two slots traded), a date is ONE-OFF (applied as
    /// two <see cref="LessonExceptionKind.Cover"/> exceptions, one on each teacher's own date).
    /// </summary>
    SlotSwap = 2,
    /// <summary>
    /// Ask a named colleague to take ONE of my lessons on ONE date, with nothing in return — the funeral,
    /// the hospital appointment, the course. Needs that colleague's agreement and a decider, exactly as a
    /// swap does, because it hands a class to somebody for a period.
    ///
    /// It grants no access: cover is a duty, and the covering teacher gets the register for that lesson
    /// and nothing else. A colleague who does not already teach the class still may not read the children's
    /// records, which is why this is not a ClassAssignment in disguise.
    /// </summary>
    LessonCover = 3
}

public enum ConfigRequestState
{
    Pending = 0,
    Approved = 1,
    Refused = 2,
    /// <summary>Taken back by the person who made it. Never a decision, so it needs no decider.</summary>
    Withdrawn = 3,
    /// <summary>
    /// Approved, and then the world had moved — the slot had gone, the class had been retired. Kept
    /// rather than deleted so "what happened to my request" always has an answer.
    /// </summary>
    Superseded = 4
}

/// <summary>What a slot looks like to the person reading the grid. Never what it looks like to everybody.</summary>
public enum SlotState
{
    /// <summary>Nothing is placed and the caller could place something here.</summary>
    Free = 0,
    /// <summary>The caller's own lesson.</summary>
    Mine = 1,
    /// <summary>
    /// Somebody else holds it. A teacher is told ONLY this — no name, no subject, no class — and a
    /// holder of timetable.manage is told who. One gate, the same code that gates building the
    /// timetable at all.
    /// </summary>
    Taken = 2,
    /// <summary>The class is busy elsewhere in this period, so the caller cannot teach it here.</summary>
    ClassBusy = 3,
    /// <summary>The caller has declared themselves unavailable.</summary>
    Unavailable = 4,
    /// <summary>Not a teaching period in the bell schedule — break, registration, nothing at all.</summary>
    NotTeaching = 5
}
