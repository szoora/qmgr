using System.Security.Cryptography;

namespace QMgr.API.Application.Services;

/// <summary>
/// The one home for temporary passwords (duty rota plan §12.2–12.3): how they are made, how long
/// they last, and the claim that marks a token issued against one.
/// </summary>
public static class TemporaryPasswords
{
    /// <summary>A temporary password stops working this long after it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>A password-change-only token lasts long enough to choose a password, no longer.</summary>
    public const int ChangeTokenMinutes = 15;

    /// <summary>
    /// The claim carried by a token issued against a temporary password. <c>PasswordChangeOnlyMiddleware</c>
    /// refuses every request bearing it except changing the password and signing out — one rule, not a
    /// check per controller.
    /// </summary>
    public const string ChangeOnlyClaim = "pwd_change";

    /// <summary>Data-protection purpose for a batch's temporary passwords while its import job is queued.</summary>
    public const string ImportProtectorPurpose = "QMgr.StaffImport.TemporaryPasswords.v1";

    // No look-alikes: no 0/O/o, 1/l/I/i, 5/S, 2/Z — a slip is read off paper and typed on a phone.
    private const string Upper = "ABCDEFGHJKMNPQRTUVWXY";
    private const string Lower = "abcdefghjkmnpqrtuvwxy";
    private const string Digits = "3467892";
    private const string All = Upper + Lower + Digits;

    /// <summary>
    /// Ten readable characters from a cryptographic source, at least one of each class. About 58 bits:
    /// far beyond online guessing under the lockout, and it only has to survive 72 hours.
    /// </summary>
    public static string Generate()
    {
        const int length = 10;
        var chars = new char[length];
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        for (var i = 3; i < length; i++)
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];

        // Fisher–Yates, so the guaranteed classes are not always in the first three places.
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}
