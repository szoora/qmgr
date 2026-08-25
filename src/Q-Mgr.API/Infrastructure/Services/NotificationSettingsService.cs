using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Service for managing notification settings and testing connections
/// </summary>
public class NotificationSettingsService : INotificationSettingsService
{
    private readonly QMgrDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationSettingsService> _logger;

    public NotificationSettingsService(
        QMgrDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationSettingsService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NotificationSettings?> GetSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _context.NotificationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);
    }

    public async Task<NotificationSettings> CreateOrUpdateSettingsAsync(NotificationSettings settings, CancellationToken cancellationToken = default)
    {
        var existing = await _context.NotificationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == settings.OrganizationId, cancellationToken);

        if (existing == null)
        {
            _context.NotificationSettings.Add(settings);
            _logger.LogInformation("Created notification settings for organization {OrganizationId}", settings.OrganizationId);
        }
        else
        {
            // Update existing settings
            existing.SmsEnabled = settings.SmsEnabled;
            existing.SmsGatewayUrl = settings.SmsGatewayUrl;
            existing.SmsApiKey = settings.SmsApiKey;
            existing.SmsUsername = settings.SmsUsername;
            existing.SmsPassword = settings.SmsPassword;
            existing.SmsSenderId = settings.SmsSenderId;
            existing.SmsCustomerId = settings.SmsCustomerId;
            existing.SmsLeadTokens = settings.SmsLeadTokens;

            existing.EmailEnabled = settings.EmailEnabled;
            existing.SmtpHost = settings.SmtpHost;
            existing.SmtpPort = settings.SmtpPort;
            existing.SmtpUseSsl = settings.SmtpUseSsl;
            existing.SmtpUsername = settings.SmtpUsername;
            existing.SmtpPassword = settings.SmtpPassword;
            existing.EmailFromAddress = settings.EmailFromAddress;
            existing.EmailFromName = settings.EmailFromName;

            existing.InAppEnabled = settings.InAppEnabled;
            existing.InAppPlaySound = settings.InAppPlaySound;
            existing.InAppRetentionDays = settings.InAppRetentionDays;

            existing.PushEnabled = settings.PushEnabled;
            existing.FirebaseProjectId = settings.FirebaseProjectId;
            existing.FirebasePrivateKey = settings.FirebasePrivateKey;
            existing.FirebaseClientEmail = settings.FirebaseClientEmail;

            existing.SmsTokenCreatedTemplate = settings.SmsTokenCreatedTemplate;
            existing.SmsTokenCalledTemplate = settings.SmsTokenCalledTemplate;
            existing.SmsReminderTemplate = settings.SmsReminderTemplate;
            existing.EmailTokenCreatedSubject = settings.EmailTokenCreatedSubject;
            existing.EmailTokenCreatedTemplate = settings.EmailTokenCreatedTemplate;
            existing.EmailTokenCalledSubject = settings.EmailTokenCalledSubject;
            existing.EmailTokenCalledTemplate = settings.EmailTokenCalledTemplate;

            existing.UpdatedBy = settings.UpdatedBy;

            _logger.LogInformation("Updated notification settings for organization {OrganizationId}", settings.OrganizationId);
            settings = existing;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<bool> TestSmsConnectionAsync(Guid organizationId, string testPhoneNumber, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null)
        {
            _logger.LogWarning("No notification settings found for organization {OrganizationId}", organizationId);
            return false;
        }

        if (string.IsNullOrEmpty(settings.SmsGatewayUrl))
        {
            _logger.LogWarning("SMS gateway URL not configured");
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(settings.SmsGatewayUrl);

            // Add authentication if configured
            if (!string.IsNullOrEmpty(settings.SmsApiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", settings.SmsApiKey);
            }
            else if (!string.IsNullOrEmpty(settings.SmsUsername) && !string.IsNullOrEmpty(settings.SmsPassword))
            {
                var credentials = Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes($"{settings.SmsUsername}:{settings.SmsPassword}"));
                client.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
            }

            var testMessage = new
            {
                Message = "Q-Mgr SMS Test: Your notification settings are configured correctly!",
                Recipient = testPhoneNumber,
                Sender = settings.SmsSenderId ?? "Q-Mgr"
            };

            var customerId = settings.SmsCustomerId ?? "default";
            var response = await client.PostAsJsonAsync($"api/sms/{customerId}/send", testMessage, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("SMS test successful to {PhoneNumber}", testPhoneNumber);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("SMS test failed: {StatusCode} - {Error}", response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMS test failed with exception");
            return false;
        }
    }

    public async Task<bool> TestEmailConnectionAsync(Guid organizationId, string testEmailAddress, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(organizationId, cancellationToken);
        if (settings == null)
        {
            _logger.LogWarning("No notification settings found for organization {OrganizationId}", organizationId);
            return false;
        }

        if (string.IsNullOrEmpty(settings.SmtpHost) || string.IsNullOrEmpty(settings.EmailFromAddress))
        {
            _logger.LogWarning("Email SMTP settings not configured");
            return false;
        }

        try
        {
            using var smtpClient = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.SmtpUseSsl,
                Credentials = !string.IsNullOrEmpty(settings.SmtpUsername)
                    ? new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
                    : null,
                Timeout = 10000 // 10 second timeout for test
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(settings.EmailFromAddress, settings.EmailFromName ?? "Q-Mgr"),
                Subject = "Q-Mgr Email Test",
                Body = @"<html>
<body style='font-family: Arial, sans-serif;'>
<h2 style='color: #00d4ff;'>Email Test Successful!</h2>
<p>Your Q-Mgr email notification settings are configured correctly.</p>
<p style='color: #666;'>This is a test email sent from the Q-Mgr notification system.</p>
</body>
</html>",
                IsBodyHtml = true
            };
            mailMessage.To.Add(testEmailAddress);

            await smtpClient.SendMailAsync(mailMessage, cancellationToken);
            _logger.LogInformation("Email test successful to {EmailAddress}", testEmailAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email test failed with exception");
            return false;
        }
    }
}
