using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QMgr.Integration;

/// <summary>
/// Verifies the signature Q-Mgr puts on the webhooks it sends, and produces the signature a
/// partner system must put on the events it pushes back to Q-Mgr's inbound endpoint.
/// <para>
/// This exists because signature verification is the one part of a webhook integration that is
/// easy to get subtly wrong and impossible to notice: comparing hex strings with <c>==</c> leaks
/// timing, re-serializing the JSON before hashing changes the bytes, and forgetting the
/// <c>sha256=</c> prefix produces a mismatch that looks like a key problem. Partners should call
/// this rather than reimplement it.
/// </para>
/// </summary>
public static class QMgrWebhookVerifier
{
    /// <summary>Header carrying the signature, on both outbound and inbound events.</summary>
    public const string SignatureHeader = "X-QMgr-Signature";

    /// <summary>Header carrying the event name on outbound deliveries.</summary>
    public const string EventHeader = "X-QMgr-Event";

    private const string Prefix = "sha256=";

    /// <summary>
    /// Computes the value for <see cref="SignatureHeader"/> over the exact bytes that will be sent.
    /// </summary>
    /// <param name="rawBody">The request body exactly as transmitted. Do not re-serialize it.</param>
    /// <param name="secret">The webhook signing secret shown once when the API client was created or rotated.</param>
    public static string ComputeSignature(ReadOnlySpan<byte> rawBody, string secret)
    {
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("A signing secret is required.", nameof(secret));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        Span<byte> hash = stackalloc byte[32];
        hmac.TryComputeHash(rawBody, hash, out _);

        var builder = new StringBuilder(Prefix, Prefix.Length + 64);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <inheritdoc cref="ComputeSignature(ReadOnlySpan{byte}, string)"/>
    public static string ComputeSignature(string rawBody, string secret)
        => ComputeSignature(Encoding.UTF8.GetBytes(rawBody ?? string.Empty), secret);

    /// <summary>
    /// True when <paramref name="signatureHeader"/> matches <paramref name="rawBody"/> under
    /// <paramref name="secret"/>. Compares in constant time, so a wrong signature cannot be
    /// recovered a byte at a time by measuring how long the rejection took. Accepts the header
    /// with or without its <c>sha256=</c> prefix.
    /// </summary>
    /// <param name="rawBody">
    /// The body exactly as received. Read it before any model binding: deserializing and
    /// re-serializing changes whitespace and property order, and the signature covers bytes.
    /// </param>
    /// <param name="signatureHeader">The received <see cref="SignatureHeader"/> value.</param>
    /// <param name="secret">The webhook signing secret for this API client.</param>
    public static bool IsValid(ReadOnlySpan<byte> rawBody, string? signatureHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        var expected = ComputeSignature(rawBody, secret);
        var presented = signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? signatureHeader
            : Prefix + signatureHeader;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
    }

    /// <inheritdoc cref="IsValid(ReadOnlySpan{byte}, string?, string)"/>
    public static bool IsValid(string rawBody, string? signatureHeader, string secret)
        => IsValid(Encoding.UTF8.GetBytes(rawBody ?? string.Empty), signatureHeader, secret);
}
