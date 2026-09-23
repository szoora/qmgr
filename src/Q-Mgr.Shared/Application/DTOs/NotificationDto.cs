namespace QMgr.Application.DTOs;

public class NotificationDto
{
    public Guid Id { get; set; }

    /// <summary>
    /// Who this notification is FOR. Every notification names one person (2026-09-23), and the
    /// Web client drops a live push whose recipient is not the signed-in user — a second line
    /// behind the server's own routing, so a wrong push can never reach a bell.
    /// </summary>
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? IconClass { get; set; }
    public string? ActionUrl { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }

    /// <summary>The NotificationEventKeys category, when the send carried one. Groups the bell and the notification centre.</summary>
    public string? EventKey { get; set; }
}
