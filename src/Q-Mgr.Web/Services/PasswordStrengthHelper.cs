using System.Text.RegularExpressions;

namespace QMgr.Web.Services;

/// <summary>
/// Client-side password-strength meter shown on Register and ResetPassword - purely a UX hint,
/// not a substitute for the real server-side policy enforced by IPasswordValidationService.
/// </summary>
public static class PasswordStrengthHelper
{
    public record Result(int Score, string CssClass, string Label);

    public static Result Calculate(string? password)
    {
        password ??= "";
        var score = 0;

        if (password.Length >= 8) score += 25;
        if (password.Length >= 12) score += 15;
        if (Regex.IsMatch(password, "[a-z]")) score += 15;
        if (Regex.IsMatch(password, "[A-Z]")) score += 15;
        if (Regex.IsMatch(password, "[0-9]")) score += 15;
        if (Regex.IsMatch(password, "[^a-zA-Z0-9]")) score += 15;

        score = Math.Min(score, 100);

        return score switch
        {
            < 30 => new Result(score, "weak", "Weak"),
            < 50 => new Result(score, "fair", "Fair"),
            < 75 => new Result(score, "good", "Good"),
            _ => new Result(score, "strong", "Strong")
        };
    }
}
