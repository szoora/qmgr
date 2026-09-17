using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.Interfaces;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Email;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The email twin of <see cref="PhoneVerificationService"/>: a six-digit, single-use, ten-minute code
/// that proves an applicant controls the address they are joining with (duty rota plan §12.4). Same
/// shape on purpose — codes in the distributed cache (they expire themselves), a cryptographic source,
/// capped attempts and a per-address send budget — so there is one pattern for "prove you hold this".
/// </summary>
public interface IEmailCodeVerificationService
{
    /// <summary>Sends a code. Returns false only when the address cannot be used at all; a send budget exhausted reads as sent.</summary>
    Task<bool> SendCodeAsync(string email, string purposeTitle, string organizationName, CancellationToken cancellationToken = default);

    /// <summary>Checks and burns the code. The message is safe to show the applicant.</summary>
    Task<(bool Verified, string? Message)> VerifyAndConsumeAsync(string email, string code, CancellationToken cancellationToken = default);
}

public class EmailCodeVerificationService : IEmailCodeVerificationService
{
    private readonly IDistributedCache _cache;
    private readonly IEmailSender _email;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<EmailCodeVerificationService> _logger;

    private const string CodePrefix = "email-verify:";
    private const string SendPrefix = "email-verify-sends:";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;
    private const int MaxSendsPerAddressPerHour = 3;

    public EmailCodeVerificationService(IDistributedCache cache, IEmailSender email, IHostEnvironment environment, ILogger<EmailCodeVerificationService> logger)
    {
        _cache = cache;
        _email = email;
        _environment = environment;
        _logger = logger;
    }

    private static string Key(string normalizedEmail)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail))).ToLowerInvariant();

    public async Task<bool> SendCodeAsync(string email, string purposeTitle, string organizationName, CancellationToken cancellationToken = default)
    {
        var normalized = RegistrationIdentity.NormalizeEmail(email);
        if (normalized == null) return false;

        var sendKey = $"{SendPrefix}{Key(normalized)}:{DateTime.UtcNow:yyyyMMddHH}";
        var sends = int.TryParse(await _cache.GetStringAsync(sendKey, cancellationToken), out var s) ? s : 0;
        if (sends >= MaxSendsPerAddressPerHour)
        {
            _logger.LogWarning("Email code send budget exhausted for an address this hour");
            return true; // answer as sent: the page must not become a probe for which addresses are busy
        }

        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
        var html = EmailTemplates.Layout(
            purposeTitle,
            null,
            new[]
            {
                $"Your verification code for joining {EmailTemplates.B(organizationName)} on {EmailTemplates.AppName} is:",
                $"<span style='font-size:26px;font-weight:700;letter-spacing:4px;'>{code}</span>",
                $"It expires in {CodeLifetime.TotalMinutes:F0} minutes. If you did not ask for this, you can ignore it."
            });

        bool sent;
        try { sent = await _email.SendAsync(email.Trim(), $"Your {EmailTemplates.AppName} verification code", html, cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "Sending an email verification code failed"); sent = false; }

        // DEVELOPMENT ONLY: the e2e suite signs up with unroutable @qmgr.local addresses, so the email never
        // arrives; it reads the code from this line in the local API log instead. The code is stored even
        // though the relay refused the message, for the same reason. Never in any other environment.
        if (_environment.IsDevelopment())
        {
            _logger.LogInformation("[DEV ONLY] Email verification code for {Email}: {Code}", normalized, code);
            sent = true;
        }

        if (sent)
        {
            await _cache.SetStringAsync($"{CodePrefix}{Key(normalized)}", $"{code}:{MaxAttempts}",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeLifetime }, cancellationToken);
        }
        await _cache.SetStringAsync(sendKey, (sends + 1).ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) }, cancellationToken);
        return true;
    }

    public async Task<(bool Verified, string? Message)> VerifyAndConsumeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var normalized = RegistrationIdentity.NormalizeEmail(email);
        if (normalized == null) return (false, "That email address is not valid.");

        var key = $"{CodePrefix}{Key(normalized)}";
        var stored = await _cache.GetStringAsync(key, cancellationToken);
        if (string.IsNullOrEmpty(stored)) return (false, "That code has expired or is not right. Ask for a new one.");

        var parts = stored.Split(':');
        var expected = parts[0];
        var remaining = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0;

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(code?.Trim() ?? string.Empty)))
        {
            remaining--;
            if (remaining <= 0)
            {
                await _cache.RemoveAsync(key, cancellationToken);
                return (false, "Too many incorrect attempts. Ask for a new code.");
            }
            await _cache.SetStringAsync(key, $"{expected}:{remaining}",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeLifetime }, cancellationToken);
            return (false, $"That code has expired or is not right. {remaining} attempt(s) left.");
        }

        await _cache.RemoveAsync(key, cancellationToken);
        return (true, null);
    }
}
