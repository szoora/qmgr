using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Sends email using the platform-wide SMTP config (<see cref="EmailSettings"/>, PlatformSetting
/// category "Email"), via the built-in <see cref="SmtpClient"/> — no third-party mail package.
/// </summary>
public class EmailSender : IEmailSender
{
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IPlatformSettingsService platformSettingsService, ILogger<EmailSender> logger)
    {
        _platformSettingsService = platformSettingsService;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var settings = await _platformSettingsService.GetSettingsAsync<EmailSettings>("Email");
        if (settings == null || string.IsNullOrEmpty(settings.SmtpHost) || string.IsNullOrEmpty(settings.FromEmail))
        {
            _logger.LogWarning("Platform email settings not configured — cannot send to {Email}", toEmail);
            return false;
        }

        try
        {
            using var smtpClient = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.UseSsl,
                Credentials = !string.IsNullOrEmpty(settings.SmtpUsername)
                    ? new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
                    : null
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(settings.FromEmail, settings.FromName ?? "Q-Mgr"),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            mailMessage.To.Add(toEmail);

            await smtpClient.SendMailAsync(mailMessage, cancellationToken);
            _logger.LogInformation("Platform email sent successfully to {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send platform email to {Email}", toEmail);
            return false;
        }
    }
}
