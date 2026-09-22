using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Entities.Platform;
using QMgr.Infrastructure.Services.Mobile;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Fills the platform's Firebase push settings from configuration when they are blank.
///
/// <para>Exactly the same shape and the same reason as <see cref="PlatformEmailDefaults"/>:
/// <c>InitializeDefaultSettingsAsync</c> returns early the moment ANY <c>PlatformSettings</c> row
/// exists, so a server that has been running since before push existed would never pick the
/// credentials up, and every handset would stay silent with nothing anywhere saying why.</para>
///
/// <para><b>It only ever fills BLANKS.</b> A project id an administrator has entered is theirs, and
/// a deploy must not silently repoint a customer's notifications at a different Firebase project.</para>
///
/// <para><b>Configuration keys (section "Push"): <c>FirebaseProjectId</c>,
/// <c>FirebaseClientEmail</c>, <c>FirebasePrivateKey</c>.</b> In production these go on the API
/// systemd unit as <c>Push__*</c> environment variables and <b>never</b> in
/// <c>appsettings.Production.json</c> — <c>install.sh</c> preserves the API's copy of that file on
/// every upgrade, so a key added there never reaches an existing server. That trap has now been paid
/// for four times: <c>MediaStorage__PublicBaseUrl</c>, <c>Email__*</c>,
/// <c>DataProtection__KeyPath</c>, and this.</para>
///
/// <para><b>A project id with no private key is treated as NOT CONFIGURED</b> rather than written in
/// half. That is the difference between "push is off" and "every push fails", and it is the same rule
/// the SMTP resolver follows for a username with no password.</para>
/// </summary>
public class PlatformPushDefaults
{
    private readonly QMgrDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformPushDefaults> _logger;

    public PlatformPushDefaults(QMgrDbContext db, IConfiguration configuration, ILogger<PlatformPushDefaults> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>The configured service account, or null when it is absent or incomplete.</summary>
    public static PlatformPushSettings? FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Push");
        var projectId = section["FirebaseProjectId"];
        var clientEmail = section["FirebaseClientEmail"];
        var privateKey = section["FirebasePrivateKey"];

        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(clientEmail)
            || string.IsNullOrWhiteSpace(privateKey))
        {
            return null;
        }

        return new PlatformPushSettings
        {
            // Enabled follows the credentials being complete. There is no separate switch to forget:
            // a configured service account that is switched off would be indistinguishable from a
            // missing one, and somebody would spend an afternoon on it.
            Enabled = true,
            FirebaseProjectId = projectId,
            FirebaseClientEmail = clientEmail,
            FirebasePrivateKey = privateKey
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var configured = FromConfiguration(_configuration);
            if (configured == null)
            {
                _logger.LogDebug("Push:Firebase* is not fully configured — leaving the platform push settings as they are.");
                return;
            }

            var row = await _db.PlatformSettings
                .FirstOrDefaultAsync(s => s.Category == PlatformPushSettings.Category, cancellationToken);

            if (row == null)
            {
                row = new PlatformSetting
                {
                    Category = PlatformPushSettings.Category,
                    SettingsJson = JsonSerializer.Serialize(configured, new JsonSerializerOptions { WriteIndented = true }),
                    UpdatedAt = DateTime.UtcNow
                };
                _db.PlatformSettings.Add(row);
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Platform push settings created from configuration for Firebase project {Project}.",
                    configured.FirebaseProjectId);
                return;
            }

            PlatformPushSettings stored;
            try
            {
                stored = JsonSerializer.Deserialize<PlatformPushSettings>(row.SettingsJson) ?? new PlatformPushSettings();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Platform push settings could not be read back; replacing them with the configured defaults.");
                stored = new PlatformPushSettings();
            }

            if (!string.IsNullOrWhiteSpace(stored.FirebaseProjectId))
            {
                _logger.LogInformation("Platform push already points at Firebase project {Project}; configuration defaults not applied.",
                    stored.FirebaseProjectId);
                return;
            }

            row.SettingsJson = JsonSerializer.Serialize(configured, new JsonSerializerOptions { WriteIndented = true });
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Platform push settings filled from configuration for Firebase project {Project}.",
                configured.FirebaseProjectId);
        }
        catch (Exception ex)
        {
            // Push configuration is not worth a dead API, the same call as the email defaults and the
            // Data Protection key-ring probe.
            _logger.LogError(ex, "Applying the configured platform push defaults failed; startup continues.");
        }
    }
}
