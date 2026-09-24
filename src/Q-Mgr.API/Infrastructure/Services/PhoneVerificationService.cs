using QMgr.Application.Branding;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.Interfaces;
using QMgr.Domain.Identity;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Sends and checks the one-time code that proves someone controls the phone number they signed up
/// with.
/// <para>
/// This is the centrepiece of the anti-abuse work rather than a nicety. In this market a SIM costs
/// money and is registered against an identity document, whereas an email address is free and
/// unlimited, so a verified number is far and away the strongest signal that two sign-ups are the
/// same person. Everything else here is a heuristic; this is close to proof.
/// </para>
/// <para>
/// Codes live in the distributed cache rather than a table: they are worthless after a few minutes,
/// and giving them their own schema would mean writing a cleanup job for data that expires itself.
/// </para>
/// </summary>
public class PhoneVerificationService : IPhoneVerificationService
{
    private readonly IDistributedCache _cache;
    private readonly INotificationService _notificationService;
    private readonly ILogger<PhoneVerificationService> _logger;

    private const string CodePrefix = "phone-verify:";
    private const string TokenPrefix = "phone-verified:";
    private const string SendPrefix = "phone-verify-sends:";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long proof of ownership stays good, so someone can finish a slow sign-up form.</summary>
    private static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Codes are single-use, and this caps guessing at the remaining attempts.</summary>
    private const int MaxAttempts = 5;

    /// <summary>Sending SMS costs money, so one number cannot be used to pump out messages.</summary>
    private const int MaxSendsPerNumberPerHour = 3;

    public PhoneVerificationService(
        IDistributedCache cache,
        INotificationService notificationService,
        ILogger<PhoneVerificationService> logger)
    {
        _cache = cache;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<PhoneVerificationSendResult> SendCodeAsync(
        Guid organizationId,
        string phone,
        CancellationToken cancellationToken = default)
    {
        var normalized = RegistrationIdentity.NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalized) || normalized.Length < 9)
        {
            return new PhoneVerificationSendResult(false, "That does not look like a valid phone number.", 0);
        }

        var sendKey = $"{SendPrefix}{normalized}:{DateTime.UtcNow:yyyyMMddHH}";
        var sends = int.TryParse(await _cache.GetStringAsync(sendKey, cancellationToken), out var s) ? s : 0;
        if (sends >= MaxSendsPerNumberPerHour)
        {
            return new PhoneVerificationSendResult(false, "Too many codes have been sent to that number. Try again later.", 0);
        }

        // Six digits, drawn from a cryptographic source rather than Random, because guessing this
        // is the whole attack.
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

        var message = $"Your {ProductBrand.Name} verification code is {code}. It expires in {CodeLifetime.TotalMinutes:F0} minutes.";

        try
        {
            var sent = await _notificationService.SendSmsAsync(organizationId, normalized, message, cancellationToken);
            if (!sent)
            {
                // No gateway configured is the normal state in development and on a fresh install.
                // Say so plainly instead of pretending a message is on its way.
                _logger.LogWarning("Could not send a verification code to {Phone}: the SMS gateway rejected it or is not configured", normalized);
                return new PhoneVerificationSendResult(false, "We could not send the code right now. Please try again shortly or contact support.", 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending a verification code to {Phone} failed", normalized);
            return new PhoneVerificationSendResult(false, "We could not send the code right now. Please try again shortly.", 0);
        }

        // Only now, once a message has genuinely gone out. Storing the code before sending would
        // leave a live code against a number that never received one, which both tells a caller
        // that a code exists (the check endpoint would start counting down attempts) and spends a
        // slot of the per-number send budget on a message nobody got.
        await _cache.SetStringAsync(
            $"{CodePrefix}{normalized}",
            $"{code}:{MaxAttempts}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeLifetime },
            cancellationToken);

        await _cache.SetStringAsync(
            sendKey,
            (sends + 1).ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) },
            cancellationToken);

        _logger.LogInformation("Sent a phone verification code to {Phone}", normalized);
        return new PhoneVerificationSendResult(true, null, (int)CodeLifetime.TotalSeconds);
    }

    public async Task<PhoneVerificationCheckResult> VerifyCodeAsync(
        string phone,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalized = RegistrationIdentity.NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalized))
        {
            return new PhoneVerificationCheckResult(false, "That does not look like a valid phone number.", null);
        }

        var key = $"{CodePrefix}{normalized}";
        var stored = await _cache.GetStringAsync(key, cancellationToken);
        if (string.IsNullOrEmpty(stored))
        {
            return new PhoneVerificationCheckResult(false, "That code has expired. Ask for a new one.", null);
        }

        var parts = stored.Split(':');
        var expected = parts[0];
        var remaining = parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0;

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(code?.Trim() ?? string.Empty)))
        {
            remaining--;
            if (remaining <= 0)
            {
                await _cache.RemoveAsync(key, cancellationToken);
                return new PhoneVerificationCheckResult(false, "Too many incorrect attempts. Ask for a new code.", null);
            }

            await _cache.SetStringAsync(
                key,
                $"{expected}:{remaining}",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeLifetime },
                cancellationToken);

            return new PhoneVerificationCheckResult(false, $"That code is not right. {remaining} attempt(s) left.", null);
        }

        // Correct. Burn the code and issue proof the sign-up endpoint can check, so the number
        // cannot simply be asserted in the final request without ever receiving a message.
        await _cache.RemoveAsync(key, cancellationToken);

        var proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await _cache.SetStringAsync(
            $"{TokenPrefix}{proof}",
            normalized,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ProofLifetime },
            cancellationToken);

        return new PhoneVerificationCheckResult(true, null, proof);
    }

    public async Task<bool> IsProofValidAsync(string? proofToken, string? phone, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proofToken)) return false;

        var normalized = RegistrationIdentity.NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalized)) return false;

        var owner = await _cache.GetStringAsync($"{TokenPrefix}{proofToken}", cancellationToken);
        return !string.IsNullOrEmpty(owner) && string.Equals(owner, normalized, StringComparison.Ordinal);
    }

    public Task ConsumeProofAsync(string? proofToken, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(proofToken)
            ? Task.CompletedTask
            : _cache.RemoveAsync($"{TokenPrefix}{proofToken}", cancellationToken);
}
