namespace QMgr.Application.Interfaces;

public interface IMediaStorageService
{
    Task<MediaUploadResult> UploadAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken = default);
    Task<Stream?> DownloadAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string filePath, CancellationToken cancellationToken = default);
    Task<string> GetUrlAsync(string filePath, TimeSpan? expiry = null, CancellationToken cancellationToken = default);
    Task<string?> GenerateThumbnailAsync(string filePath, CancellationToken cancellationToken = default);
}

public record MediaUploadResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? FileUrl { get; init; }
    public string? ThumbnailUrl { get; init; }
    public long FileSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}
