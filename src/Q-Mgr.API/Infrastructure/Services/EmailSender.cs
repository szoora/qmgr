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
    private readonly ISmtpProfileResolver _smtp;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(ISmtpProfileResolver smtp, ILogger<EmailSender> logger)
    {
        _smtp = smtp;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        // Null tenant settings == the platform account. This mail (password resets, invitations)
        // belongs to no organization, so there is nothing to fall back FROM — but it resolves
        // through the same one place as tenant mail so there is a single answer to "which account
        // does Q-Mgr send through?".
        var profile = await _smtp.ResolveAsync(null);
        if (profile == null)
        {
            _logger.LogWarning("Platform email settings not configured — cannot send to {Email}", toEmail);
            return false;
        }

        try
        {
            using var smtpClient = new SmtpClient(profile.Host, profile.Port)
            {
                EnableSsl = profile.UseSsl,
                Credentials = !string.IsNullOrEmpty(profile.Username)
                    ? new NetworkCredential(profile.Username, profile.Password)
                    : null
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(profile.FromEmail, profile.FromName),
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
