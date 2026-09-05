namespace QMgr.Domain.Enums;

public enum RosterImportRowOutcome
{
    /// <summary>New Student and/or new guardian VisitorProfile created.</summary>
    Created = 0,

    /// <summary>Matched an existing Student (by StudentCode) and/or guardian (by phone/email/ID) — fields refreshed from the row.</summary>
    Updated = 1,

    /// <summary>Exact duplicate of an earlier row in the SAME file (same StudentCode + same guardian identifier) — processed once, this row skipped.</summary>
    DuplicateInFile = 2,

    /// <summary>Failed validation (missing required field, no usable guardian contact) — nothing written for this row.</summary>
    Failed = 3,

    /// <summary>
    /// The row duplicates something that was already stored before this import started, not
    /// another row in the same file — nothing was written for it.
    ///
    /// Kept distinct from <see cref="DuplicateInFile"/> on purpose: "you listed this twice" is a
    /// mistake to fix in the spreadsheet, whereas "this was already recorded" usually means the
    /// file overlaps a period somebody has already entered by hand, which is a different thing to
    /// do about it. Both still count toward the job's DuplicateCount, which tallies rows skipped
    /// as duplicates regardless of which side the duplicate came from.
    ///
    /// Appended, never inserted — these values are persisted as integers on RosterImportJobEntry.
    /// </summary>
    AlreadyExists = 4,

    /// <summary>
    /// The row was deliberately left alone — it is not an error and nothing about it is wrong.
    ///
    /// This exists for batch operations, where "nothing to do" is an ordinary and frequent
    /// outcome: a student already in the final class has no class to be promoted to, one whose
    /// class is not in the branch's list cannot be advanced safely, and one already holding the
    /// value being set needs no write. Folding those into <see cref="Failed"/> would put a normal
    /// result in the error column and make a clean promotion look like it half broke.
    ///
    /// Appended, never inserted.
    /// </summary>
    Skipped = 5
}
