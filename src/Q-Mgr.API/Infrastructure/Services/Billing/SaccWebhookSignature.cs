using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QMgr.Infrastructure.Services.Billing;

/// <summary>
/// Verifies the sacc.ug gateway's webhook signature — the one place it is checked (2026-09-19).
///
/// The gateway (CRMApi <c>WebhookService.ComputeSignature</c>) sends
/// <c>X-Webhook-Signature: t={unix seconds},v1={hex HMAC-SHA256(secret, "{t}.{body}")}</c> over the exact
/// bytes it POSTs — the scheme Stripe uses. A delivery is accepted only when the signature matches in
/// fixed time AND its timestamp is within the replay window, so a captured delivery cannot be
/// replayed later to re-open a settled payment.
/// </summary>
public static class SaccWebhookSignature
{
    /// <summary>How far a delivery's timestamp may be from now, either way.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);

    public static bool Verify(string body, string? header, string secret, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(secret)) { reason = "no webhook secret is configured"; return false; }
        if (string.IsNullOrWhiteSpace(header)) { reason = "missing X-Webhook-Signature"; return false; }

        string? t = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t") t = value;
            else if (key == "v1") signatures.Add(value);
        }

        if (t == null || !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        { reason = "the signature has no timestamp"; return false; }
        if (signatures.Count == 0) { reason = "the signature has no v1 value"; return false; }

        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((now - sent).Duration() > ReplayWindow) { reason = "the delivery is outside the replay window"; return false; }

        var expected = Compute(body, secret, t);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        foreach (var sig in signatures)
        {
            var given = Encoding.ASCII.GetBytes(sig.ToLowerInvariant());
            if (given.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(given, expectedBytes))
                return true;
        }

        reason = "the signature does not match";
        return false;
    }

    /// <summary>hex HMAC-SHA256(secret, "{t}.{body}"), lower case. Public so the e2e stub's signing and
    /// this check can be proven to agree.</summary>
    public static string Compute(string body, string secret, string timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
    }
}
