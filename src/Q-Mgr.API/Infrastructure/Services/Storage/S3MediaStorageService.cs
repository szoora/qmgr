using Amazon.S3;
using Amazon.S3.Model;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// S3-compatible media storage provider (works against real AWS S3 and any
/// S3-compatible endpoint — MinIO, DigitalOcean Spaces, etc. — via ServiceUrl).
/// Selected via DependencyInjection.cs when MediaStorage:Provider="S3".
///
/// Not live-tested against a real bucket as of 2026-08-19 — no cloud credentials
/// exist in this dev environment. Compile-verified and structurally mirrors
/// LocalDiskMediaStorageService's contract only; treat as groundwork for
/// Production Rollout Plan Stage 3, not as a confirmed-working path, until
/// someone runs it against a real bucket.
/// </summary>
public class S3MediaStorageService : IMediaStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string? _publicBaseUrl;
    private readonly ILogger<S3MediaStorageService> _logger;

    private const string KeyPrefix = "media/";

    public S3MediaStorageService(IAmazonS3 s3Client, IConfiguration configuration, ILogger<S3MediaStorageService> logger)
    {
        _s3Client = s3Client;
        _bucketName = configuration["MediaStorage:S3:BucketName"]
            ?? throw new InvalidOperationException("MediaStorage:S3:BucketName must be set when MediaStorage:Provider is \"S3\".");
        _publicBaseUrl = configuration["MediaStorage:S3:PublicBaseUrl"]; // e.g. a CDN/CloudFront domain fronting the bucket
        _logger = logger;
    }

    public async Task<MediaUploadResult> UploadAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        var key = $"{KeyPrefix}{Guid.NewGuid()}{extension}";

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = fileStream,
                ContentType = contentType,
                AutoCloseStream = false
            };

            await _s3Client.PutObjectAsync(request, cancellationToken);

            return new MediaUploadResult
            {
                Success = true,
                FilePath = key,
                FileUrl = await GetUrlAsync(key, null, cancellationToken),
                FileSizeBytes = fileStream.CanSeek ? fileStream.Length : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 upload failed for key {Key}", key);
            return new MediaUploadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _s3Client.GetObjectAsync(_bucketName, filePath, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await _s3Client.DeleteObjectAsync(_bucketName, filePath, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete S3 object {Key}", filePath);
            return false;
        }
    }

    /// <summary>
    /// If a public CDN base URL is configured, uses that directly (the normal
    /// production setup — a CloudFront/CDN domain fronting a private bucket).
    /// Otherwise falls back to a pre-signed URL scoped to `expiry` (default 1 hour),
    /// which requires the bucket to NOT be public.
    /// </summary>
    public Task<string> GetUrlAsync(string filePath, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_publicBaseUrl))
            return Task.FromResult($"{_publicBaseUrl.TrimEnd('/')}/{filePath}");

        var url = _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = filePath,
            Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromHours(1))
        });

        return Task.FromResult(url);
    }

    /// <summary>
    /// No dedicated thumbnail generation exists yet — same as the Local provider.
    /// </summary>
    public Task<string?> GenerateThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
