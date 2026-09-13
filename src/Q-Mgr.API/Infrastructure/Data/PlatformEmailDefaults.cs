using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Domain.Entities.Platform;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Fills the platform's Email/SMTP settings from configuration when they are blank.
///
/// Why this exists rather than a default in the seeder alone: <c>InitializeDefaultSettingsAsync</c>
/// returns early if ANY PlatformSettings row exists, so on a server that has been running since
/// before the platform account was chosen, the Email row keeps whatever it was created with —
/// blank — for ever, and every tenant that has not entered its own SMTP details sends nothing.
/// The same shape as <see cref="UploadLinkRepair"/>: a startup reconciliation for a setting an
/// existing install could not otherwise pick up.
///
/// It only ever fills BLANKS. An administrator who has entered their own host, credentials or
/// from-address in Platform Settings keeps them; this never overwrites a deliberate choice, so a
/// deploy cannot silently repoint a customer's mail through a different account.
///
/// Configuration keys (section "Email"): SmtpHost, SmtpPort, SmtpUsername, SmtpPassword, FromEmail,
/// FromName, UseSsl. In production these are set on the API systemd unit as Email__* environment
/// variables, not in appsettings.Production.json, because install.sh preserves that file on upgrade
/// and a key added to it would never reach an existing server — the same reasoning as
/// MediaStorage__PublicBaseUrl.
///
/// A failure is logged and never stops the API from starting: mail configuration is not worth a
/// dead API.
/// </summary>
public class PlatformEmailDefaults
{
    private readonly QMgrDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformEmailDefaults> _logger;

    public PlatformEmailDefaults(QMgrDbContext db, IConfiguration configuration, ILogger<PlatformEmailDefaults> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The configured platform mailbox, or null when no host is configured. Also used by the
    /// seeder so a fresh install and an existing one end up with identical values.
    /// </summary>
    public static EmailSettings? FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Email");
        var host = section["SmtpHost"];
        if (string.IsNullOrWhiteSpace(host)) return null;

        // A username with no password can never authenticate, so treat that as "not configured"
        // rather than writing it in and turning every send into a relay-denied failure. This is the
        // shape a deploy that forgot to pass the password takes, and it is the difference between
        // "email is off" and "email is broken" in the delivery log.
        if (!string.IsNullOrWhiteSpace(section["SmtpUsername"]) && string.IsNullOrWhiteSpace(section["SmtpPassword"]))
            return null;

        return new EmailSettings
        {
            SmtpHost = host,
            SmtpPort = int.TryParse(section["SmtpPort"], out var port) ? port : 587,
            SmtpUsername = section["SmtpUsername"] ?? string.Empty,
            SmtpPassword = section["SmtpPassword"] ?? string.Empty,
            FromEmail = section["FromEmail"] ?? section["SmtpUsername"] ?? string.Empty,
            FromName = string.IsNullOrWhiteSpace(section["FromName"]) ? "Q-Mgr" : section["FromName"]!,
            UseSsl = !bool.TryParse(section["UseSsl"], out var ssl) || ssl
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var configured = FromConfiguration(_configuration);
            if (configured == null)
            {
                _logger.LogDebug("Email:SmtpHost is not configured — leaving the platform email settings as they are.");
                return;
            }

            var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Category == "Email", cancellationToken);
            if (row == null)
            {
                // No row at all — the seeder has not run yet on a fresh database and will write
                // these same values itself. Nothing to reconcile.
                _logger.LogDebug("No platform Email settings row yet; the initializer will create it.");
                return;
            }

            EmailSettings stored;
            try
            {
                stored = JsonSerializer.Deserialize<EmailSettings>(row.SettingsJson) ?? new EmailSettings();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Platform Email settings could not be read back; replacing them with the configured defaults.");
                stored = new EmailSettings();
            }

            // Only the blanks. A host already chosen by an administrator is the answer, full stop —
            // and if the host is theirs, the credentials and from-address belong to it, so those are
            // left alone too rather than mixed with the platform's.
            if (!string.IsNullOrWhiteSpace(stored.SmtpHost))
            {
                _logger.LogInformation("Platform email already points at {Host}; configuration defaults not applied.", stored.SmtpHost);
                return;
            }

            row.SettingsJson = JsonSerializer.Serialize(configured, new JsonSerializerOptions { WriteIndented = true });
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Platform email settings filled from configuration: {Host}:{Port} as {From} (credentials {Creds}).",
                configured.SmtpHost, configured.SmtpPort, configured.FromEmail,
                string.IsNullOrEmpty(configured.SmtpPassword) ? "NOT set" : "set");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the configured platform email defaults failed; startup continues.");
        }
    }
}
