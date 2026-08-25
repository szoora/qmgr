using Microsoft.AspNetCore.Http;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// Default media storage provider — writes to the API's own wwwroot/uploads/media,
/// same as ContentController's inline logic did before this was extracted behind
/// IMediaStorageService. Deliberately not the multi-instance-safe choice (see
/// ContentController.UploadMediaContent's own doc comment on why uploads live on
/// the API, not the Web instance) — that's what MediaStorage:Provider="S3" is for.
/// Selected via DependencyInjection.cs when MediaStorage:Provider is unset or "Local".
/// </summary>
public class LocalDiskMediaStorageService : IMediaStorageService
{
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<LocalDiskMediaStorageService> _logger;

    private const string RelativeFolder = "uploads/media";

    public LocalDiskMediaStorageService(
        IWebHostEnvironment webHostEnvironment,
        IHttpContextAccessor httpContextAccessor,
        ILogger<LocalDiskMediaStorageService> logger)
    {
        _webHostEnvironment = webHostEnvironment;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string UploadsDirectory => Path.Combine(_webHostEnvironment.WebRootPath, "uploads", "media");

    public async Task<MediaUploadResult> UploadAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(UploadsDirectory);

            var extension = Path.GetExtension(fileName);
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var diskPath = Path.Combine(UploadsDirectory, uniqueFileName);
            var relativePath = $"{RelativeFolder}/{uniqueFileName}";

            await using (var destination = new FileStream(diskPath, FileMode.Create))
            {
                await fileStream.CopyToAsync(destination, cancellationToken);
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
    /// </summary>
    public Task<string> GetUrlAsync(string filePath, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        var relativePath = filePath.StartsWith(RelativeFolder) ? filePath : $"{RelativeFolder}/{Path.GetFileName(filePath)}";

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
