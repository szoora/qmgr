namespace QMgr.Application.Interfaces;

/// <summary>
/// Platform-level email sender, backed by <c>PlatformSetting</c>'s Email category (SMTP config
/// shared by the whole platform, not any single tenant). Use this for mail that isn't scoped to
/// an organization yet — e.g. pre-verification signup email — as opposed to
/// <see cref="INotificationService"/>, which sends on behalf of a specific tenant using that
/// tenant's own <c>NotificationSettings</c>.
/// </summary>
public interface IEmailSender
{
    Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
