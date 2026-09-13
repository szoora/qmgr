using Microsoft.EntityFrameworkCore;

namespace QMgr.Infrastructure.Data;

/// <summary>
/// Repoints stored upload links that were saved with a loopback host onto MediaStorage:PublicBaseUrl.
///
/// Until 2026-09-11 LocalDiskMediaStorageService built every upload's link from the incoming
/// request's host. In production uploads reach the API from Q-Mgr.Web's server-side HttpClient over
/// the internal loopback, so every link was saved as http://127.0.0.1:{ApiPort}/uploads/..., an
/// address no browser can reach (signage showed "503 while retrieving PDF"). New uploads now use the
/// public base; this fixes the rows written before that.
///
/// Runs at startup only when the public base is configured (production), so development data is
/// never touched. Idempotent: a repaired link no longer matches the pattern, so later starts update
/// nothing. A failure is logged and never stops the API from starting.
/// </summary>
public class UploadLinkRepair
{
    // Anchored for single-link columns; unanchored for links embedded in article HTML.
    private const string LoopbackUploadLink = @"^https?://(127\.0\.0\.1|localhost)(:[0-9]+)?/uploads/";
    private const string LoopbackUploadLinkAnywhere = @"https?://(127\.0\.0\.1|localhost)(:[0-9]+)?/uploads/";

    // One complete statement per column an IMediaStorageService upload link is stored in. Written out
    // in full rather than assembled from table/column names, so no SQL text is ever built at runtime;
    // {0} is the pattern and {1} the replacement, both sent as parameters. Schema-qualified because
    // raw SQL does not get HasDefaultSchema("qmgr") applied.
    private static readonly (string Target, string Sql)[] SingleLinkUpdates =
    {
        ("media_content.FileUrl",
            "UPDATE qmgr.media_content SET \"FileUrl\" = regexp_replace(\"FileUrl\", {0}, {1}) WHERE \"FileUrl\" ~ {0}"),
        ("media_content.ThumbnailUrl",
            "UPDATE qmgr.media_content SET \"ThumbnailUrl\" = regexp_replace(\"ThumbnailUrl\", {0}, {1}) WHERE \"ThumbnailUrl\" ~ {0}"),
        ("WelfareAttachments.FileUrl",
            "UPDATE qmgr.\"WelfareAttachments\" SET \"FileUrl\" = regexp_replace(\"FileUrl\", {0}, {1}) WHERE \"FileUrl\" ~ {0}"),
        ("broadcast_attachments.Url",
            "UPDATE qmgr.broadcast_attachments SET \"Url\" = regexp_replace(\"Url\", {0}, {1}) WHERE \"Url\" ~ {0}"),
        ("doc_articles.CoverImageUrl",
            "UPDATE qmgr.doc_articles SET \"CoverImageUrl\" = regexp_replace(\"CoverImageUrl\", {0}, {1}) WHERE \"CoverImageUrl\" ~ {0}"),
        ("students.PhotoUrl",
            "UPDATE qmgr.students SET \"PhotoUrl\" = regexp_replace(\"PhotoUrl\", {0}, {1}) WHERE \"PhotoUrl\" ~ {0}"),
        ("visitor_profiles.PhotoUrl",
            "UPDATE qmgr.visitor_profiles SET \"PhotoUrl\" = regexp_replace(\"PhotoUrl\", {0}, {1}) WHERE \"PhotoUrl\" ~ {0}"),
    };

    private const string ArticleBodyUpdate =
        "UPDATE qmgr.doc_articles SET \"BodyHtml\" = regexp_replace(\"BodyHtml\", {0}, {1}, 'g') WHERE \"BodyHtml\" ~ {0}";

    private readonly QMgrDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UploadLinkRepair> _logger;

    public UploadLinkRepair(QMgrDbContext db, IConfiguration configuration, ILogger<UploadLinkRepair> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var publicBase = _configuration["MediaStorage:PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBase))
            return;

        var replacement = publicBase + "/uploads/";

        try
        {
            var total = 0;

            foreach (var (target, sql) in SingleLinkUpdates)
            {
                var updated = await _db.Database.ExecuteSqlRawAsync(
                    sql, new object[] { LoopbackUploadLink, replacement }, cancellationToken);

                if (updated > 0)
                    _logger.LogInformation("Repointed {Count} loopback upload link(s) in {Target} to {PublicBase}", updated, target, publicBase);
                total += updated;
            }

            var bodies = await _db.Database.ExecuteSqlRawAsync(
                ArticleBodyUpdate, new object[] { LoopbackUploadLinkAnywhere, replacement }, cancellationToken);
            if (bodies > 0)
                _logger.LogInformation("Repointed loopback upload links inside {Count} doc article body(ies) to {PublicBase}", bodies, publicBase);
            total += bodies;

            _logger.LogInformation("Upload link repair complete: {Total} row(s) updated", total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload link repair failed; stored links still point at a loopback address");
        }
    }
}
