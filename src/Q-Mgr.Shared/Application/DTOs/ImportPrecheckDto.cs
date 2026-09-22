namespace QMgr.Application.DTOs;

/// <summary>
/// TWO PEOPLE WHO MIGHT BE ONE — raised by a precheck, answered by the reader, never by the system.
///
/// <para>A school's roll genuinely holds two children called the same name, and a member of staff
/// really can share a name with a colleague. So this is a question put before the import is sent,
/// with a confirmation the reader has to give; it is not a refusal and must never become one. The
/// standing rule it follows is the sign-up guard's: near-proof blocks, a likeness asks.</para>
/// </summary>
public record ImportPossibleDuplicateDto
{
    /// <summary>The name as the file writes it — what the reader will look for in their own sheet.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Why it was raised, in the reader's terms: "this name is on two rows under different
    /// admission numbers", or "already on file as MH/0112". One sentence, no jargon.
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>True when both are rows of THIS file; false when the likeness is to somebody already on file.</summary>
    public bool WithinFile { get; init; }
}

/// <summary>
/// One person the import would land on rather than create, and WHAT IT WOULD CHANGE ABOUT THEM.
///
/// <para>The field names only — never the stored values. A reader deciding whether to overwrite
/// needs to know that this file moves a class and a guardian's phone number; they do not need the
/// current national ID of every member of staff read back to their browser to be told so.</para>
/// </summary>
public record ImportExistingPersonDto
{
    /// <summary>The key this person was matched on — an admission number, a staff number or an address.</summary>
    public string MatchedOn { get; init; } = string.Empty;

    /// <summary>Their name as it stands on file, so the reader recognises them.</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>The fields this file would overwrite, in the reader's words ("class", "guardian phone"). Empty means nothing in the file is different.</summary>
    public List<string> Changes { get; init; } = new();
}
