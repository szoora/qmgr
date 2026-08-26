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
    Failed = 3
}
