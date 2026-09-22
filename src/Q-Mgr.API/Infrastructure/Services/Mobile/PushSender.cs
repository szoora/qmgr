using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;

namespace QMgr.Infrastructure.Services.Mobile;

/// <summary>
/// Sends a notification to a person's handsets through Firebase Cloud Messaging.
///
/// <para><b>Why the credentials are the PLATFORM's and not the tenant's.</b> <c>NotificationSettings</c>
/// has carried <c>FirebaseProjectId</c>, <c>FirebasePrivateKey</c> and <c>FirebaseClientEmail</c>
/// per tenant since the schema was written, and <b>those three columns cannot work</b>: one installed
/// app ships one <c>google-services.json</c> and is therefore registered with exactly one FCM
/// sender, so a school's own Firebase project has no route to that binary. Push credentials belong
/// where the app's identity belongs — the platform. Same shape as the platform mailbox, and read
/// through <c>PlatformSettings</c> for the same reason.</para>
///
/// <para><b>FCM HTTP v1, not the legacy server key.</b> The legacy endpoint was retired in 2024, so
/// there is no simpler option. v1 wants a Google OAuth access token, which is obtained by signing a
/// JWT with the service account's RSA key and exchanging it — about forty lines below, using only
/// <c>System.Security.Cryptography</c> and <c>System.Text.Json</c>. <b>No new server dependency</b>,
/// which is this project's standing constraint and the reason the Firebase Admin SDK is not here.</para>
///
/// <para><b>It degrades, it never fails a notification.</b> A handset that cannot be reached is a
/// <see cref="ChannelSendOutcome.Skipped"/> with a reason, never a failure — the delivery log's
/// standing rule, because marking "this person has no phone" as a red failure against every message
/// makes the log useless. Same call as <c>VisitorsController.TryIssueVisitToken</c>.</para>
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Push to every live, permitted handset this user holds.
    /// </summary>
    Task<ChannelSendResult> SendAsync(Guid userId, string title, string body, string? actionUrl,
                                      CancellationToken ct = default);

    /// <summary>Whether the platform has FCM credentials at all. Read by the tenant probe.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
}

public sealed class PushSender : IPushSender
{
    public const string HttpClientName = "fcm";

    private const string TokenCacheKey = "fcm:access-token";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly IPlatformSettingsService _platformSettings;
    private readonly IDeviceSessionService _devices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PushSender> _log;

    public PushSender(IPlatformSettingsService platformSettings, IDeviceSessionService devices,
                      IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<PushSender> log)
    {
        _platformSettings = platformSettings;
        _devices = devices;
        _httpFactory = httpFactory;
        _cache = cache;
        _log = log;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => (await ReadCredentialsAsync(ct)) is not null;

    public async Task<ChannelSendResult> SendAsync(Guid userId, string title, string body, string? actionUrl,
                                                   CancellationToken ct = default)
    {
        var creds = await ReadCredentialsAsync(ct);
        if (creds is null)
            return ChannelSendResult.Skipped("Push is not configured on this platform.");

        var targets = await _devices.PushTargetsAsync(new[] { userId }, ct);
        if (targets.Count == 0)
        {
            // Not a failure. Most people have no handset signed in, and a red row against every
            // notification they receive would bury the ones that are real.
            return ChannelSendResult.Skipped("No device on this account can receive a notification.");
        }

        string accessToken;
        try
        {
            accessToken = await AccessTokenAsync(creds, ct);
        }
        catch (Exception ex)
        {
            // A credential or network problem IS a failure: it is our end, it is fixable, and it
            // should show up in the delivery log as something to act on.
            _log.LogError(ex, "Could not obtain an FCM access token");
            return ChannelSendResult.Failed("Could not authenticate with the push service.");
        }

        var http = _httpFactory.CreateClient(HttpClientName);
        var sent = 0;
        var failures = new List<string>();

        foreach (var device in targets)
        {
            try
            {
                var outcome = await SendOneAsync(http, creds.ProjectId, accessToken, device.PushToken!,
                                                 title, body, actionUrl, ct);

                if (outcome == SendOutcome.Sent) { sent++; continue; }

                if (outcome == SendOutcome.TokenGone)
                {
                    // FCM says this registration is dead — the app was uninstalled, or the device
                    // was wiped. Clear the TOKEN and keep the SESSION: the session may be perfectly
                    // live, and signing somebody out because their notifications stopped is the
                    // wrong trade. The next check-in supplies a new token and clears the flag.
                    await _devices.RetirePushTokenAsync(device.Id, ct);
                    continue;
                }

                failures.Add(device.DeviceId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Push to device {DeviceId} failed", device.DeviceId);
                failures.Add(device.DeviceId);
            }
        }

        if (sent > 0) return ChannelSendResult.Sent();

        // Every target was a dead token: nothing was delivered, but nothing is broken either, and
        // the tokens have been retired so it will not repeat.
        if (failures.Count == 0)
            return ChannelSendResult.Skipped("Every device on this account has an expired push registration.");

        return ChannelSendResult.Failed($"The push service rejected {failures.Count} device(s).");
    }

    private enum SendOutcome { Sent, TokenGone, Failed }

    private async Task<SendOutcome> SendOneAsync(HttpClient http, string projectId, string accessToken,
                                                 string token, string title, string body, string? actionUrl,
                                                 CancellationToken ct)
    {
        // The v1 message shape. `data` carries the deep link so a tap opens the page the
        // notification is about rather than the dashboard; `notification` is what the OS displays.
        var payload = new
        {
            message = new
            {
                token,
                notification = new { title, body },
                data = new Dictionary<string, string>
                {
                    ["actionUrl"] = actionUrl ?? string.Empty
                },
                android = new
                {
                    priority = "high",
                    notification = new
                    {
                        // The OS channel the app declares. A notification naming a channel the app
                        // has not created is silently dropped on Android 8+, so this string is a
                        // CONTRACT with PushChannels in the app, not a label.
                        channel_id = "qmgr-alerts",
                        click_action = "QMGR_NOTIFICATION_CLICK"
                    }
                },
                apns = new
                {
                    payload = new { aps = new { sound = "default" } }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return SendOutcome.Sent;

        var text = await response.Content.ReadAsStringAsync(ct);

        // 404 UNREGISTERED and 400 INVALID_ARGUMENT on the token are the two "this registration is
        // gone" answers. Matched on the body rather than the status alone, because a 400 can also
        // mean the message itself was malformed — and retiring a good token because of our own bug
        // would silently stop a handset ever being notified again.
        var gone = response.StatusCode == System.Net.HttpStatusCode.NotFound
                   || text.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("registration-token-not-registered", StringComparison.OrdinalIgnoreCase);

        if (gone) return SendOutcome.TokenGone;

        _log.LogWarning("FCM refused a message: {Status} {Body}", (int)response.StatusCode, Clip(text, 400));
        return SendOutcome.Failed;
    }

    // ── Google OAuth: sign a JWT with the service account key and exchange it ─────────────────

    private async Task<string> AccessTokenAsync(FcmCredentials creds, CancellationToken ct)
    {
        if (_cache.TryGetValue(TokenCacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached!;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = creds.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = TokenEndpoint,
            ["iat"] = now,
            ["exp"] = now + 3600
        }));

        var unsigned = $"{header}.{claims}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(creds.PrivateKey);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var assertion = $"{unsigned}.{Base64Url(signature)}";

        var http = _httpFactory.CreateClient(HttpClientName);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        using var response = await http.PostAsync(TokenEndpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google refused the service-account assertion: {(int)response.StatusCode} {Clip(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Google returned no access token.");

        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        // Held slightly short of its real life so a request never starts with a token that expires
        // mid-flight.
        _cache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expires - 300)));

        return token!;
    }

    // ── Credentials ──────────────────────────────────────────────────────────

    private sealed record FcmCredentials(string ProjectId, string ClientEmail, string PrivateKey);

    private async Task<FcmCredentials?> ReadCredentialsAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _platformSettings.GetSettingsAsync<PlatformPushSettings>(PlatformPushSettings.Category);
            if (settings is null || !settings.Enabled) return null;

            if (string.IsNullOrWhiteSpace(settings.FirebaseProjectId)
                || string.IsNullOrWhiteSpace(settings.FirebaseClientEmail)
                || string.IsNullOrWhiteSpace(settings.FirebasePrivateKey))
            {
                // A project id with no key is treated as NOT CONFIGURED rather than written in.
                // That is the difference between "push is off" and "every push fails", and it is the
                // same rule the SMTP resolver follows for a username with no password.
                return null;
            }

            // A PEM pasted through a JSON field arrives with literal \n. Restoring them is not
            // cosmetic: ImportFromPem refuses a single-line key, and the failure reads as a bad
            // credential rather than a bad paste.
            var pem = settings.FirebasePrivateKey!.Replace("\\n", "\n");

            return new FcmCredentials(settings.FirebaseProjectId!, settings.FirebaseClientEmail!, pem);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read the platform push settings");
            return null;
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Clip(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}

/// <summary>
/// The platform's FCM service account. One estate, one sender — see <see cref="PushSender"/> for why
/// this cannot be per tenant.
///
/// <para><b><see cref="FirebasePrivateKey"/> must be in <c>SecretProperties</c> on
/// <c>PlatformSettingsController</c></b>, or it is returned in clear to a SuperAdmin the way the SMTP
/// password and the Stripe keys were until 2026-09-15.</para>
/// </summary>
public class PlatformPushSettings
{
    public const string Category = "Push";

    /// <summary>
    /// Off until somebody configures it. A dropped request must not switch push ON, which is the
    /// same asymmetry <c>AttributionRemoved</c> carries and for the same reason.
    /// </summary>
    public bool Enabled { get; set; }

    public string? FirebaseProjectId { get; set; }
    public string? FirebaseClientEmail { get; set; }

    /// <summary>The service account's RSA private key, PEM. A SECRET — mask it on read.</summary>
    public string? FirebasePrivateKey { get; set; }
}
