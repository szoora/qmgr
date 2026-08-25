namespace QMgr.Web.Components.Shared.UI;

/// <summary>
/// Severity of a toast notification — drives which QIcon/color variant ToastHost renders.
/// </summary>
public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public record ToastMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ToastSeverity Severity { get; init; } = ToastSeverity.Info;
    public string Title { get; init; } = string.Empty;
    public string? Message { get; init; }
    public int DurationMs { get; init; } = 5000;
}

/// <summary>
/// Single source of truth for toast notifications across the app — replaces Radzen's
/// NotificationService. Same (severity, title, message) call shape so pages migrate with
/// a near-mechanical find/replace. Rendered by ToastHost, one instance per layout root.
/// </summary>
public interface IToastService
{
    event Action<ToastMessage>? OnShow;
    event Action<Guid>? OnDismiss;

    void Notify(ToastSeverity severity, string title, string? message = null, int durationMs = 5000);
    void Dismiss(Guid id);
}

public class ToastService : IToastService
{
    public event Action<ToastMessage>? OnShow;
    public event Action<Guid>? OnDismiss;

    public void Notify(ToastSeverity severity, string title, string? message = null, int durationMs = 5000)
    {
        OnShow?.Invoke(new ToastMessage
        {
            Severity = severity,
            Title = title,
            Message = message,
            DurationMs = durationMs
        });
    }

    public void Dismiss(Guid id)
    {
        OnDismiss?.Invoke(id);
    }
}
