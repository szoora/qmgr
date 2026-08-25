using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

public record ContactDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? TelegramChatId { get; init; }
    public string? Tags { get; init; }
    public ContactSource Source { get; init; }
    public bool OptedOut { get; init; }
    public DateTime? OptedOutAt { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record CreateContactRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TelegramChatId { get; set; }
    public string? Tags { get; set; }
}

public record BroadcastDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public BroadcastChannel Channel { get; init; }
    public string? Subject { get; init; }
    public string MessageBody { get; init; } = string.Empty;
    public string? AudienceTagFilter { get; init; }
    public BroadcastStatus Status { get; init; }
    public DateTime? ScheduledAt { get; init; }
    public DateTime? SendStartedAt { get; init; }
    public DateTime? SendCompletedAt { get; init; }
    public int TotalRecipients { get; init; }
    public int SentCount { get; init; }
    public int FailedCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<BroadcastAttachmentDto> Attachments { get; init; } = new();
}

public record BroadcastAttachmentDto
{
    public Guid Id { get; init; }
    public string Url { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
}

public record CreateBroadcastRequest
{
    public string Name { get; set; } = string.Empty;
    public BroadcastChannel Channel { get; set; }
    public string? Subject { get; set; }
    public string MessageBody { get; set; } = string.Empty;
    public string? AudienceTagFilter { get; set; }
    public DateTime? ScheduledAt { get; set; } // null = send as soon as the job next runs
}
