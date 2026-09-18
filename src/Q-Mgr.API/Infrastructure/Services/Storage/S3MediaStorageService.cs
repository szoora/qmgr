using Amazon.S3;
using Amazon.S3.Model;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// S3-compatible media storage provider (works against real AWS S3 and any
/// S3-compatible endpoint — MinIO, DigitalOcean Spaces, etc. — via ServiceUrl).
/// Selected via DependencyInjection.cs when MediaStorage:Provider="S3".
///
/// Still not live-tested against a real bucket (no cloud credentials exist in this dev
/// environment), so treat it as groundwork for Production Rollout Plan Stage 3 rather than a
/// confirmed-working path until someone runs it against one. What IS now shared with
/// LocalDiskMediaStorageService, rather than merely mirrored in shape, is the part a bucket
/// cannot be trusted to re-check: the stored extension comes from
/// <see cref="UploadFileTypes.ResolveStoredExtension"/>, a <c>.pdf</c> must really start with
/// "%PDF-", the refusal wording is the same, and <see cref="MediaUploadResult.FilePath"/> is the
/// same <c>uploads/media/{file}</c> relative path — which is what
/// <see cref="UploadAuthorizer"/> classifies on, and what an S3-stored row therefore has to carry
/// or it is an orphan. The 2026-09-15 upload-type security fix landed on the local provider only;
/// this class had kept the client's own extension, with no allow-list and no magic-byte check, for
/// as long as it existed.
///
/// One thing is NOT shared and is worth knowing before switching a tenant to a bucket: SERVING is
/// still local-only — <c>UploadsController</c> reads bytes off
/// <see cref="LocalDiskMediaStorageService.ResolveStoreDirectory"/>, not through this interface —
/// so an S3 store needs the gate taught to stream from the bucket (or to redirect to a pre-signed
/// URL) before a gated file can be read at all.
/// </summary>
public class S3MediaStorageService : IMediaStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string? _publicBaseUrl;
    private readonly ILogger<S3MediaStorageService> _logger;

    /// <summary>
    /// The object key IS the relative path <c>UploadsController</c> serves and
    /// <c>UploadAuthorizer.LookUpAsync</c> matches on (<c>uploads/media/{file}</c>), so one string
    /// is the key, the stored <c>FilePath</c> and the URL's path. It used to be a bare
    /// <c>media/</c>, which can never equal what the authorizer builds — every S3-stored row would
    /// have classified as an orphan: token-only, no bearer access, and the Library's own preview
    /// (which fetches with the JWT, not a signed link) broken for every file in the bucket.
    /// </summary>
    private const string KeyPrefix = UploadAccessService.RelativeFolder + "/";

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
        // The STORED extension comes from the allow-list, never from the client's file name alone:
        // the serving side derives Content-Type from it (see UploadFileTypes for the XSS this
        // closed). A type on no list is refused here, whatever the controller allowed. Word for
        // word the local provider's rule — a bucket is not a second security boundary, it is the
        // same one, and this class was the half of it that never got the fix.
        var extension = UploadFileTypes.ResolveStoredExtension(fileName, contentType);
        if (extension == null)
            return new MediaUploadResult { Success = false, ErrorMessage = $"File type '{(string.IsNullOrEmpty(contentType) ? Path.GetExtension(fileName) : contentType)}' is not allowed." };

        var key = $"{KeyPrefix}{Guid.NewGuid()}{extension}";

        // The Content-Type the object is STORED with is the allow-list's own, not the client's
        // declaration: a bucket fronted by a CDN serves what it was told at PUT time, with no
        // UploadsController in the path to correct it.
        var storedContentType = UploadFileTypes.ContentTypeFor(key) ?? "application/octet-stream";

        Stream body = fileStream;
        Stream? buffered = null;
        try
        {
            // A ".pdf" that is not a PDF is served as application/pdf and rendered by pdf.js. The
            // local provider writes the file and then checks the bytes on disk; there is no disk
            // here and UploadFileTypes.HasPdfMagic takes a path, so the same five bytes are read
            // off the stream BEFORE the object is put — which is better anyway: nothing rejected
            // ever reaches the bucket, so there is no delete to fail.
            if (extension == ".pdf")
            {
                var (isPdf, rewound) = await EnsurePdfAsync(body, cancellationToken);
                if (!ReferenceEquals(rewound, body)) buffered = rewound;
                if (!isPdf)
                    return new MediaUploadResult { Success = false, ErrorMessage = "The file is not a PDF." };
                body = rewound;
            }

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = body,
                ContentType = storedContentType,
                AutoCloseStream = false
            };

            await _s3Client.PutObjectAsync(request, cancellationToken);

            return new MediaUploadResult
            {
                Success = true,
                // Key and relative path are one string now, so what is stored is what the
                // authorizer looks up. GetUrlAsync turns it into the browser-facing URL.
                FilePath = key,
                FileUrl = await GetUrlAsync(key, null, cancellationToken),
                FileSizeBytes = body.CanSeek ? body.Length : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 upload failed for key {Key}", key);
            return new MediaUploadResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            // Only ever the copy this method made; the caller still owns the stream it passed in.
            if (buffered != null) await buffered.DisposeAsync();
        }
    }

    /// <summary>
    /// A PDF starts with "%PDF-" (UploadFileTypes.HasPdfMagic's rule, read from a stream instead of
    /// a path). Returns the stream to hand S3, positioned where it was: the same stream when it can
    /// seek, otherwise a buffered copy, because the five bytes read here cannot be pushed back in
    /// front of a forward-only body.
    /// </summary>
    private static async Task<(bool IsPdf, Stream Body)> EnsurePdfAsync(Stream source, CancellationToken ct)
    {
        var head = new byte[5];

        if (source.CanSeek)
        {
            var origin = source.Position;
            var read = await source.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            source.Position = origin;
            return (HasPdfMagic(head, read), source);
        }

        var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        copy.Position = 0;
        var readFromCopy = await copy.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        copy.Position = 0;
        return (HasPdfMagic(head, readFromCopy), copy);
    }

    private static bool HasPdfMagic(ReadOnlySpan<byte> head, int read)
        => read == 5 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F' && head[4] == (byte)'-';

    /// <summary>
    /// The object key for a stored path. Callers pass the stored <c>FilePath</c>, which already IS
    /// the key; a bare file name is accepted too, the same latitude
    /// LocalDiskMediaStorageService.GetUrlAsync allows, so nothing breaks on a caller that only has
    /// the name (and so the older <c>media/{file}</c> keys of any bucket written before this change
    /// still resolve under the folder they are read from today).
    /// </summary>
    private static string ToKey(string filePath)
        => filePath.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)
            ? filePath
            : KeyPrefix + Path.GetFileName(filePath);

    public async Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _s3Client.GetObjectAsync(_bucketName, ToKey(filePath), cancellationToken);
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
            await _s3Client.DeleteObjectAsync(_bucketName, ToKey(filePath), cancellationToken);
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
        var key = ToKey(filePath);

        if (!string.IsNullOrEmpty(_publicBaseUrl))
            return Task.FromResult($"{_publicBaseUrl.TrimEnd('/')}/{key}");

        var url = _s3Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
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
