using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using QMgr.Application.Interfaces;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers;

/// <summary>
/// Serves uploaded files with a per-file authorisation decision.
///
/// Until 2026-09-15 uploads lived under the API's wwwroot and <c>UseStaticFiles()</c> served them
/// 28 lines before <c>UseAuthentication()</c>: no identity, no check, no audit, and a URL that
/// once leaked worked for ever. The store is now outside wwwroot (see
/// <see cref="LocalDiskMediaStorageService.ResolveStoreDirectory"/>) and every byte comes through
/// here. The URL path is unchanged — <c>/uploads/media/{file}</c> — so nothing stored, emailed or
/// printed went dead; only the thing answering at that path changed.
///
/// The decision, in order:
///  1. Public by intent (signage media, broadcast attachments, help-centre images): served, with
///     a day of cache. A poster on a wall was put there by choice.
///  2. Otherwise a valid access token in <c>?t=</c> (<see cref="IUploadAccessService"/>): served,
///     no caching. Tokens are minted only at the point a DTO is handed to a caller who has already
///     passed the owning record's checks, which is what makes a permission check on the record
///     mean something.
///  3. Otherwise a signed-in user who may read the owning record (<see cref="IUploadAuthorizer"/>):
///     served. This is the curl / integration path; browsers cannot attach the JWT to an image.
///  4. Otherwise 401 for an anonymous caller and 404 for a signed-in one — never 403, because a
///     403 confirms the file exists, the welfare module's standing rule.
///
/// Rate limiting: <c>get:/uploads/*</c> is whitelisted in <c>ServiceExtensions.AddRateLimiting</c>;
/// one display loading a playlist fetches many files from a single address.
/// </summary>
[ApiController]
[Route("uploads/media")]
[AllowAnonymous]
public class UploadsController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly IUploadAuthorizer _authorizer;
    private readonly IUploadAccessService _access;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<UploadsController> _logger;

    public UploadsController(
        IUploadAuthorizer authorizer,
        IUploadAccessService access,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<UploadsController> logger)
    {
        _authorizer = authorizer;
        _access = access;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet("{fileName}")]
    [HttpHead("{fileName}")]
    public async Task<IActionResult> Get(string fileName, [FromQuery(Name = IUploadAccessService.TokenQueryKey)] string? token, CancellationToken cancellationToken)
    {
        // A plain file name and nothing else. The store is one flat folder of GUID-named files;
        // anything with a separator or a parent reference is not one of ours.
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
            return NotFound();

        var store = LocalDiskMediaStorageService.ResolveStoreDirectory(_configuration, _environment);
        var diskPath = Path.Combine(store, fileName);
        if (!System.IO.File.Exists(diskPath))
            return NotFound();

        var classification = await _authorizer.ClassifyAsync(fileName, cancellationToken);

        if (classification.IsPublic)
            return Serve(diskPath, isPublic: true);

        if (_access.IsValid(fileName, token))
            return Serve(diskPath, isPublic: false);

        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        if (await _authorizer.CanCurrentUserReadAsync(classification, cancellationToken))
            return Serve(diskPath, isPublic: false);

        _logger.LogInformation("Refused upload {FileName} ({Kind}) to an authenticated caller outside its scope", fileName, classification.Kind);
        return NotFound();
    }

    private IActionResult Serve(string diskPath, bool isPublic)
    {
        if (!ContentTypes.TryGetContentType(diskPath, out var contentType))
            contentType = "application/octet-stream";

        var info = new FileInfo(diskPath);
        var lastModified = new DateTimeOffset(info.LastWriteTimeUtc);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");

        Response.Headers[HeaderNames.CacheControl] = isPublic
            ? "public, max-age=86400"
            : "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // An uploaded SVG or HTML document rendered inline on this origin would run its scripts
        // here. Anything that can carry script is offered as a download, never rendered.
        if (contentType is "image/svg+xml" or "text/html" or "application/xhtml+xml")
        {
            Response.Headers[HeaderNames.ContentDisposition] = $"attachment; filename=\"{info.Name}\"";
            contentType = "application/octet-stream";
        }

        // Range processing matters: <video> seeks and pdf.js range-loads.
        return PhysicalFile(diskPath, contentType, lastModified, etag, enableRangeProcessing: true);
    }
}
