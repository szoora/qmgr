using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Marketing;

/// <summary>
/// A person a tenant can send campaign broadcasts to. Org-scoped (not branch-scoped) — a
/// contact gathered at one branch is reachable from a broadcast targeting the whole org.
/// OptedOut is the single most important field on this entity: no send path may ever write
/// to a contact where OptedOut is true, and OptOutToken exists specifically so a recipient
/// can opt out via a public, unauthenticated link with no account/login required.
/// </summary>
public class Contact : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    // Telegram's Bot API can only message a chat the recipient has already started with the bot —
    // there is no way to message an arbitrary phone number or username directly, so this has to be
    // captured separately (e.g. the contact DMs the bot /start, which the bot's webhook records
    // here) rather than derived from Phone/Email like the other channels.
    public string? TelegramChatId { get; set; }
    public string? Tags { get; set; } // comma-separated, simple segmentation

    public ContactSource Source { get; set; } = ContactSource.Manual;

    public bool OptedOut { get; set; }
    public DateTime? OptedOutAt { get; set; }
    public Guid OptOutToken { get; set; } = Guid.NewGuid();

    public virtual Organization.Organization? Organization { get; set; }
    public virtual Organization.Branch? Branch { get; set; }
}
