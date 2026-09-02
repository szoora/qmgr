namespace QMgr.Domain.Enums;

/// <summary>
/// Distinguishes a routine follow-up note from a formal statement (student/staff/witness account
/// of what happened) on the same append-only WelfareNote thread — added post-MVP rather than a
/// new entity, since a statement is structurally just a note with two extra facts attached.
/// </summary>
public enum WelfareNoteKind
{
    Note = 0,
    Statement = 1
}
