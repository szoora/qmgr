using Microsoft.EntityFrameworkCore;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Fills <c>media_content.FilePath</c> for uploads made before that column was populated.
///
/// <c>UploadAuthorizer</c> classifies a media file by <c>FilePath</c> ONLY — the column the server
/// writes on upload and a client cannot set — because matching on <c>FileUrl</c> let anyone with
/// content.create publish a gated file by creating a URL-linked media row that named it (security
/// review, 2026-09-15). Older uploads carry a null <c>FilePath</c> and a <c>FileUrl</c> that ends in
/// our own store path; without this backfill they would classify as orphans (token-only) and
/// vanish from the display screens.
///
/// Only rows whose link points INTO our upload store and whose storage type is Local are touched;
/// a genuinely external link (YouTube, Drive) never matches. Idempotent; a failure is logged and
/// never stops the API from starting. Runs before the first request can be served.
/// </summary>
public class MediaFilePathBackfill
{
    private const string Sql =
        "UPDATE qmgr.media_content SET \"FilePath\" = 'uploads/media/' || regexp_replace(\"FileUrl\", '^.*/uploads/media/', '') " +
        "WHERE \"FilePath\" IS NULL AND \"StorageType\" = 0 AND \"FileUrl\" ~ '/uploads/media/[^/?#]+$'";

    private readonly QMgrDbContext _db;
    private readonly ILogger<MediaFilePathBackfill> _logger;

    public MediaFilePathBackfill(QMgrDbContext db, ILogger<MediaFilePathBackfill> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _db.Database.ExecuteSqlRawAsync(Sql, cancellationToken);
            if (updated > 0)
                _logger.LogInformation("Media FilePath backfill: {Count} legacy upload row(s) now carry their store path", updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media FilePath backfill failed; legacy uploads without a FilePath are gated (token-only) until it succeeds");
        }
    }
}
