using Microsoft.AspNetCore.Http;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// Default media storage provider — writes to a directory OUTSIDE the API's wwwroot
/// (MediaStorage:LocalPath; see <see cref="ResolveStoreDirectory"/> for the default), served by
/// <c>UploadsController</c> with a per-file authorisation decision. Until 2026-09-15 the store
/// was wwwroot/uploads/media, which the static-file middleware served before authentication
/// ran — every welfare attachment and visitor photograph was readable by anyone holding its
/// URL. <see cref="QMgr.Infrastructure.Data.UploadStoreRelocation"/> moves an existing store
/// across at startup.
///
/// Deliberately not the multi-instance-safe choice (see ContentController.UploadMediaContent's
/// own doc comment on why uploads live on the API, not the Web instance) — that's what
/// MediaStorage:Provider="S3" is for. Selected via DependencyInjection.cs when
/// MediaStorage:Provider is unset or "Local".
/// </summary>
public class LocalDiskMediaStorageService : IMediaStorageService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<LocalDiskMediaStorageService> _logger;

    // The browser-facing origin that serves /uploads/ (production: https://qmgr.cashbook.ug, where
    // nginx routes /uploads/ to this API). Unset in development, where the request host is right.
    private readonly string? _publicBaseUrl;
    private readonly string _storeDirectory;

    /// <summary>
    /// The URL path stays <c>uploads/media/{file}</c> even though the disk location moved: every
    /// stored link in the database carries it, and keeping the route means no link had to be
    /// rewritten and nothing a customer emailed or printed went dead.
    /// </summary>
    private const string RelativeFolder = UploadAccessService.RelativeFolder;

    public LocalDiskMediaStorageService(
        IWebHostEnvironment webHostEnvironment,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<LocalDiskMediaStorageService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _publicBaseUrl = configuration["MediaStorage:PublicBaseUrl"];
        _storeDirectory = ResolveStoreDirectory(configuration, webHostEnvironment);
        _logger = logger;
    }

    /// <summary>
    /// Where the bytes live. MediaStorage:LocalPath when set — production sets it in the API's
    /// systemd unit to $UploadsPath/media, the directory that already persisted across deploys —
    /// otherwise App_Data/uploads/media under the content root. Never under wwwroot: anything
    /// there is served by the static-file middleware ahead of every authorisation check.
    /// </summary>
    public static string ResolveStoreDirectory(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["MediaStorage:LocalPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(environment.ContentRootPath, "App_Data", "uploads", "media");
    }

    /// <summary>The wwwroot folder the store used to be, so the relocation step and the serving controller agree on it.</summary>
    public static string LegacyStoreDirectory(IWebHostEnvironment environment)
        => Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads", "media");

    private string UploadsDirectory => _storeDirectory;

    /// <summary>Absolute disk path of a stored file name, or null when the name is not a plain file name.</summary>
    public string? DiskPathFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.Contains("..")) return null;
        return Path.Combine(UploadsDirectory, fileName);
    }

    public async Task<MediaUploadResult> UploadAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(UploadsDirectory);

            // The STORED extension comes from the allow-list, never from the client's file name
            // alone: the serving side derives Content-Type from it (see UploadFileTypes for the
            // XSS this closed). A type on no list is refused here, whatever the controller allowed.
            var extension = UploadFileTypes.ResolveStoredExtension(fileName, contentType);
            if (extension == null)
                return new MediaUploadResult { Success = false, ErrorMessage = $"File type '{(string.IsNullOrEmpty(contentType) ? Path.GetExtension(fileName) : contentType)}' is not allowed." };

            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var diskPath = Path.Combine(UploadsDirectory, uniqueFileName);
            var relativePath = $"{RelativeFolder}/{uniqueFileName}";

            await using (var destination = new FileStream(diskPath, FileMode.Create))
            {
                await fileStream.CopyToAsync(destination, cancellationToken);
            }

            // A ".pdf" that is not a PDF is served as application/pdf and rendered by pdf.js — or,
            // declared as something else, would once have been served as whatever the client said.
            if (extension == ".pdf" && !UploadFileTypes.HasPdfMagic(diskPath))
            {
                File.Delete(diskPath);
                return new MediaUploadResult { Success = false, ErrorMessage = "The file is not a PDF." };
            }

            return new MediaUploadResult
            {
                Success = true,
                FilePath = relativePath,
                FileUrl = await GetUrlAsync(relativePath, null, cancellationToken),
                FileSizeBytes = new FileInfo(diskPath).Length
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local disk upload failed for {FileName}", fileName);
            return new MediaUploadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var diskPath = ToDiskPath(filePath);
        if (!File.Exists(diskPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var diskPath = ToDiskPath(filePath);
            if (File.Exists(diskPath))
                File.Delete(diskPath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete local media file {FilePath}", filePath);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Absolute, not relative: served from the API's own origin regardless of which
    /// Web instance is rendering the request — matches the reasoning already documented
    /// on ContentController.UploadMediaContent. `expiry` is meaningless for a plain
    /// static file and is ignored (unlike the S3 provider, where it drives a pre-signed URL).
    ///
    /// MediaStorage:PublicBaseUrl wins when set. The request host is only right when the browser
    /// itself called the API. In production every upload arrives from Q-Mgr.Web's server-side
    /// HttpClient over the internal loopback (ApiBaseUrl, http://127.0.0.1:{ApiPort}), so the
    /// request host is an address no browser can reach — and it was being saved into every
    /// uploaded file's link, which is why signage showed "503 while retrieving PDF
    /// http://127.0.0.1:8586/uploads/…".
    /// </summary>
    public Task<string> GetUrlAsync(string filePath, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        var relativePath = filePath.StartsWith(RelativeFolder) ? filePath : $"{RelativeFolder}/{Path.GetFileName(filePath)}";

        if (!string.IsNullOrWhiteSpace(_publicBaseUrl))
            return Task.FromResult($"{_publicBaseUrl.TrimEnd('/')}/{relativePath}");

        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null)
        {
            // No HttpContext (e.g. a background job) — best effort, no host to anchor to.
            return Task.FromResult($"/{relativePath}");
        }

        return Task.FromResult($"{request.Scheme}://{request.Host}/{relativePath}");
    }

    /// <summary>
    /// No dedicated thumbnail generation exists yet — matches pre-extraction behavior,
    /// where image thumbnails just reuse the full file's own URL.
    /// </summary>
    public Task<string?> GenerateThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    private string ToDiskPath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return Path.Combine(UploadsDirectory, fileName);
    }
}
