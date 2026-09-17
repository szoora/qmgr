using QMgr.API.Domain.Entities;
using QMgr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace QMgr.API.Application.Services;

public interface IPasswordValidationService
{
    /// <param name="organizationName">The school's own name; a password built on it is refused (NIST SP 800-63B-4 §3.1.1.2, context-specific words).</param>
    /// <param name="alsoRefuse">Further values the password must not be — e.g. the temporary password it replaces.</param>
    Task<PasswordValidationResult> ValidatePasswordAsync(string password, string? username = null, string? email = null,
        string? organizationName = null, IEnumerable<string>? alsoRefuse = null);
    Task<PasswordPolicySettings> GetPasswordPolicyAsync();
    Task UpdatePasswordPolicyAsync(PasswordPolicySettings policy, Guid updatedBy);
    Task<SecuritySettings> GetSecuritySettingsAsync();
    Task UpdateSecuritySettingsAsync(SecuritySettings settings, Guid updatedBy);
}

public class PasswordValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ErrorMessage => Errors.Any() ? string.Join(" ", Errors) : null;
}

public class PasswordValidationService : IPasswordValidationService
{
    private readonly IPlatformConfigurationService _configService;
    private readonly ILogger<PasswordValidationService> _logger;

    // Common passwords list (top 100 most common passwords)
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "123456", "12345678", "qwerty", "abc123", "monkey", "1234567", "letmein",
        "trustno1", "dragon", "baseball", "111111", "iloveyou", "master", "sunshine", "ashley",
        "bailey", "passw0rd", "shadow", "123123", "654321", "superman", "qazwsx", "michael",
        "football", "welcome", "jesus", "ninja", "mustang", "password1", "123456789", "admin",
        "solo", "starwars", "121212", "freedom", "whatever", "qwertyuiop", "trustno1",
        "google", "1234567890", "000000", "zxcvbnm", "1q2w3e4r", "qwerty123", "password123"
    };

    public PasswordValidationService(
        IPlatformConfigurationService configService,
        ILogger<PasswordValidationService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task<SecuritySettings> GetSecuritySettingsAsync()
    {
        return await _configService.GetSettingsAsync<SecuritySettings>("Security");
    }

    public async Task UpdateSecuritySettingsAsync(SecuritySettings settings, Guid updatedBy)
    {
        await _configService.UpdateSettingsAsync("Security", settings, updatedBy);
        _logger.LogInformation("Security settings updated by user {UserId}", updatedBy);
    }

    public async Task<PasswordPolicySettings> GetPasswordPolicyAsync()
    {
        var securitySettings = await GetSecuritySettingsAsync();
        return securitySettings.PasswordPolicy;
    }

    public async Task UpdatePasswordPolicyAsync(PasswordPolicySettings policy, Guid updatedBy)
    {
        var securitySettings = await GetSecuritySettingsAsync();
        securitySettings.PasswordPolicy = policy;
        await UpdateSecuritySettingsAsync(securitySettings, updatedBy);
        _logger.LogInformation("Password policy updated by user {UserId}", updatedBy);
    }

    public async Task<PasswordValidationResult> ValidatePasswordAsync(string password, string? username = null, string? email = null,
        string? organizationName = null, IEnumerable<string>? alsoRefuse = null)
    {
        var result = new PasswordValidationResult();
        var policy = await GetPasswordPolicyAsync();

        // 0. The blocklist (duty rota plan §12.2; NIST SP 800-63B-4 §3.1.1.2). It runs whatever the
        //    policy's toggles say: a guessable password is refused by rule, not by preference, and this
        //    is what stops a school onboarding everyone on "Staff2026!" — which satisfies every
        //    character-class rule above and is exactly the shared default the plan rules out.
        var blocked = PasswordBlocklist.Check(password, username, email, organizationName, alsoRefuse);
        if (blocked != null)
            result.Errors.Add(blocked);

        // 1. Check minimum length
        if (password.Length < policy.MinimumLength)
        {
            result.Errors.Add($"Password must be at least {policy.MinimumLength} characters long.");
        }

        // 2. Check maximum length (prevent BCrypt DoS)
        if (password.Length > policy.MaximumLength)
        {
            result.Errors.Add($"Password must not exceed {policy.MaximumLength} characters.");
        }

        // 3. Check uppercase requirement
        if (policy.RequireUppercase && !password.Any(char.IsUpper))
        {
            result.Errors.Add("Password must contain at least one uppercase letter.");
        }

        // 4. Check lowercase requirement
        if (policy.RequireLowercase && !password.Any(char.IsLower))
        {
            result.Errors.Add("Password must contain at least one lowercase letter.");
        }

        // 5. Check digits requirement
        if (policy.RequireDigits && !password.Any(char.IsDigit))
        {
            result.Errors.Add("Password must contain at least one number.");
        }

        // 6. Check special characters requirement
        if (policy.RequireSpecialCharacters)
        {
            var hasSpecialChar = password.Any(c => policy.AllowedSpecialCharacters.Contains(c));
            if (!hasSpecialChar)
            {
                result.Errors.Add($"Password must contain at least one special character ({policy.AllowedSpecialCharacters}).");
            }
        }

        // 7. Check for common passwords
        if (policy.PreventCommonPasswords && CommonPasswords.Contains(password))
        {
            result.Errors.Add("This password is too common. Please choose a more unique password.");
        }

        // 8. Check for user info in password
        if (policy.PreventUserInfoInPassword)
        {
            if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("Password must not contain your username.");
            }

            if (!string.IsNullOrEmpty(email))
            {
                var emailUser = email.Split('@')[0];
                if (password.Contains(emailUser, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add("Password must not contain your email address.");
                }
            }
        }

        // 9. Check minimum unique characters
        var uniqueChars = password.Distinct().Count();
        if (uniqueChars < policy.MinimumUniqueCharacters)
        {
            result.Errors.Add($"Password must contain at least {policy.MinimumUniqueCharacters} unique characters.");
        }

        result.IsValid = !result.Errors.Any();
        return result;
    }
}
