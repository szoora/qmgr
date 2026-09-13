using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Platform;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The single home for "which SMTP account does this organization actually send through?".
///
/// There were three copies of this resolution before — <see cref="NotificationService"/>'s send,
/// <see cref="NotificationSettingsService"/>'s test-send, and <see cref="EmailSender"/>'s
/// platform-only path — and they disagreed: the first two required the tenant to have configured
/// its own SMTP host and silently sent nothing otherwise, while the third only ever used the
/// platform account. A tenant that had never opened Notification Settings therefore had email
/// switched on and delivered none of it. This is that rule, written once.
/// </summary>
public interface ISmtpProfileResolver
{
    /// <summary>
    /// Resolves the account to send through. Pass the organization's own settings, or null for the
    /// platform account (password resets, invitations — mail that belongs to no tenant).
    /// Returns null when neither the tenant nor the platform has a usable account configured.
    /// </summary>
    Task<SmtpProfile?> ResolveAsync(NotificationSettings? tenantSettings);
}

/// <summary>
/// A resolved, ready-to-use SMTP account. Everything <see cref="System.Net.Mail.SmtpClient"/> and
/// the message header need, with no further null-checking at the call site.
/// </summary>
public sealed record SmtpProfile(
    string Host,
    int Port,
    bool UseSsl,
    string? Username,
    string? Password,
    string FromEmail,
    string FromName,
    bool IsPlatformRelay);

public class SmtpProfileResolver : ISmtpProfileResolver
{
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ILogger<SmtpProfileResolver> _logger;

    public SmtpProfileResolver(IPlatformSettingsService platformSettings, ILogger<SmtpProfileResolver> logger)
    {
        _platformSettings = platformSettings;
        _logger = logger;
    }

    public async Task<SmtpProfile?> ResolveAsync(NotificationSettings? tenantSettings)
    {
        // A tenant that has entered its own SMTP host sends as itself, unchanged. That is the
        // white-label case and it takes precedence over anything the platform holds.
        if (!string.IsNullOrWhiteSpace(tenantSettings?.SmtpHost)
            && !string.IsNullOrWhiteSpace(tenantSettings.EmailFromAddress))
        {
            return new SmtpProfile(
                Host: tenantSettings.SmtpHost!,
                Port: tenantSettings.SmtpPort,
                UseSsl: tenantSettings.SmtpUseSsl,
                Username: tenantSettings.SmtpUsername,
                Password: tenantSettings.SmtpPassword,
                FromEmail: tenantSettings.EmailFromAddress!,
                FromName: string.IsNullOrWhiteSpace(tenantSettings.EmailFromName) ? "Q-Mgr" : tenantSettings.EmailFromName!,
                IsPlatformRelay: false);
        }

        var platform = await _platformSettings.GetSettingsAsync<EmailSettings>("Email");
        if (platform == null || string.IsNullOrWhiteSpace(platform.SmtpHost) || string.IsNullOrWhiteSpace(platform.FromEmail))
        {
            _logger.LogWarning("No SMTP account available: the organization has none configured and the platform Email settings are incomplete.");
            return null;
        }

        // The From address MUST be the platform's own mailbox, not the tenant's. Real relays
        // (IONOS, Google, Microsoft) reject a From that is not the authenticated account, so
        // carrying the tenant's address here would turn a working relay into a 5xx on every send.
        // The tenant's DISPLAY NAME is kept, so the recipient still sees the school's name on the
        // message even though it is delivered by the platform account.
        var displayName = !string.IsNullOrWhiteSpace(tenantSettings?.EmailFromName)
            ? tenantSettings!.EmailFromName!
            : (string.IsNullOrWhiteSpace(platform.FromName) ? "Q-Mgr" : platform.FromName);

        return new SmtpProfile(
            Host: platform.SmtpHost,
            Port: platform.SmtpPort,
            UseSsl: platform.UseSsl,
            Username: platform.SmtpUsername,
            Password: platform.SmtpPassword,
            FromEmail: platform.FromEmail,
            FromName: displayName,
            IsPlatformRelay: true);
    }
}
