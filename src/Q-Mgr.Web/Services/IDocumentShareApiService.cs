using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QMgr.Application.DTOs;

namespace QMgr.Web.Services;

/// <summary>
/// The Document Library and secure sharing, from the Web side. Two halves: the staff endpoints
/// (authenticated, org-scoped) and the public viewer endpoints (anonymous, slug-addressed).
///
/// THROWS on failure with the server's own message — the convention every Web API service here
/// follows except IQueueApiService (see CLAUDE.md for why that one is the cautionary tale).
/// </summary>
public interface IDocumentShareApiService
{
    // ---- Library documents ----
    Task<MediaContentDto> UploadDocumentAsync(Guid organizationId, Stream file, string fileName, string? name, string? summary, string? publishedFrom, bool shareable);
    Task<MediaContentDto> UpdatePublishingAsync(Guid mediaId, UpdateMediaPublishingRequest request);

    /// <summary>Raising needs library.publish; lowering needs documents.share.manage and a reason.</summary>
    Task<MediaContentDto> UpdateClassificationAsync(Guid mediaId, UpdateMediaClassificationRequest request);

    // ---- Links ----
    Task<List<DocumentShareDto>> GetSharesAsync(Guid mediaId);
    Task<DocumentShareIssuedDto> CreateShareAsync(Guid mediaId, CreateDocumentShareRequest request);
    Task<DocumentShareDto> UpdateShareAsync(Guid mediaId, Guid shareId, UpdateDocumentShareRequest request);
    Task<DocumentShareDto> RevokeShareAsync(Guid mediaId, Guid shareId, string? reason);

    // ---- Activity ----
    Task<DocumentActivityDto> GetActivityAsync(Guid mediaId);
    Task<List<DocumentShareEventDto>> GetShareEventsAsync(Guid mediaId, Guid shareId);
    Task<string> ExportActivityCsvAsync(Guid mediaId);

    // ---- Policy ----
    Task<DocumentSharingPolicyDto> GetPolicyAsync(Guid organizationId);
    Task<DocumentSharingPolicyDto> UpdatePolicyAsync(Guid organizationId, DocumentSharingPolicyDto policy);

    // ---- The public viewer (/s/{slug}) ----
    /// <summary>Null when the slug is unknown.</summary>
    Task<SharedDocumentGateDto?> GetGateAsync(string slug);
    /// <summary>
    /// The viewer argument is the BROWSER's address and agent (cascaded from App.razor). Without
    /// it the API sees this Web server as the reader — 127.0.0.0 and no browser, in production.
    /// </summary>
    Task<OpenSharedDocumentResponse> OpenAsync(string slug, OpenSharedDocumentRequest request, ViewerRequestInfo? viewer = null);
    Task<OpenSharedDocumentResponse> ResumeAsync(string slug, string sessionToken, ViewerRequestInfo? viewer = null);
    Task RecordPageAsync(string slug, SharedDocumentEventRequest request, ViewerRequestInfo? viewer = null);

    /// <summary>Absolute, on the API's PUBLIC origin: the browser fetches these itself.</summary>
    string ContentUrl(string slug, string contentToken);
    string DownloadUrl(string slug, string sessionToken);
}

public class DocumentShareApiService : IDocumentShareApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentShareApiService> _logger;

    public DocumentShareApiService(HttpClient http, JsonSerializerOptions json, IConfiguration configuration, ILogger<DocumentShareApiService> logger)
    {
        _http = http;
        _json = json;
        _configuration = configuration;
        _logger = logger;
    }

    private async Task<T> ReadOrThrow<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(_json))!;
    }

    // ---- Library documents ----

    public async Task<MediaContentDto> UploadDocumentAsync(Guid organizationId, Stream file, string fileName, string? name, string? summary, string? publishedFrom, bool shareable)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(file);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(streamContent, "file", fileName);
        if (!string.IsNullOrWhiteSpace(name)) content.Add(new StringContent(name), "name");
        if (!string.IsNullOrWhiteSpace(summary)) content.Add(new StringContent(summary), "summary");
        if (!string.IsNullOrWhiteSpace(publishedFrom)) content.Add(new StringContent(publishedFrom), "publishedFrom");
        content.Add(new StringContent(shareable ? "true" : "false"), "shareable");

        var response = await _http.PostAsync($"api/v1/organizations/{organizationId}/media/upload", content);
        return await ReadOrThrow<MediaContentDto>(response);
    }

    public async Task<MediaContentDto> UpdatePublishingAsync(Guid mediaId, UpdateMediaPublishingRequest request)
        => await ReadOrThrow<MediaContentDto>(await _http.PutAsJsonAsync($"api/v1/media/{mediaId}/publishing", request, _json));

    public async Task<MediaContentDto> UpdateClassificationAsync(Guid mediaId, UpdateMediaClassificationRequest request)
        => await ReadOrThrow<MediaContentDto>(await _http.PutAsJsonAsync($"api/v1/media/{mediaId}/classification", request, _json));

    // ---- Links ----

    public async Task<List<DocumentShareDto>> GetSharesAsync(Guid mediaId)
        => await ReadOrThrow<List<DocumentShareDto>>(await _http.GetAsync($"api/v1/media/{mediaId}/shares"));

    public async Task<DocumentShareIssuedDto> CreateShareAsync(Guid mediaId, CreateDocumentShareRequest request)
        => await ReadOrThrow<DocumentShareIssuedDto>(await _http.PostAsJsonAsync($"api/v1/media/{mediaId}/shares", request, _json));

    public async Task<DocumentShareDto> UpdateShareAsync(Guid mediaId, Guid shareId, UpdateDocumentShareRequest request)
        => await ReadOrThrow<DocumentShareDto>(await _http.PutAsJsonAsync($"api/v1/media/{mediaId}/shares/{shareId}", request, _json));

    public async Task<DocumentShareDto> RevokeShareAsync(Guid mediaId, Guid shareId, string? reason)
    {
        var message = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/media/{mediaId}/shares/{shareId}")
        {
            Content = JsonContent.Create(new RevokeDocumentShareRequest { Reason = reason }, options: _json)
        };
        return await ReadOrThrow<DocumentShareDto>(await _http.SendAsync(message));
    }

    // ---- Activity ----

    public async Task<DocumentActivityDto> GetActivityAsync(Guid mediaId)
        => await ReadOrThrow<DocumentActivityDto>(await _http.GetAsync($"api/v1/media/{mediaId}/activity"));

    public async Task<List<DocumentShareEventDto>> GetShareEventsAsync(Guid mediaId, Guid shareId)
        => await ReadOrThrow<List<DocumentShareEventDto>>(await _http.GetAsync($"api/v1/media/{mediaId}/shares/{shareId}/events"));

    public async Task<string> ExportActivityCsvAsync(Guid mediaId)
    {
        var response = await _http.GetAsync($"api/v1/media/{mediaId}/activity/export");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ApiErrorService.GetErrorMessageAsync(response));
        return await response.Content.ReadAsStringAsync();
    }

    // ---- Policy ----

    public async Task<DocumentSharingPolicyDto> GetPolicyAsync(Guid organizationId)
        => await ReadOrThrow<DocumentSharingPolicyDto>(await _http.GetAsync($"api/v1/organizations/{organizationId}/document-sharing/policy"));

    public async Task<DocumentSharingPolicyDto> UpdatePolicyAsync(Guid organizationId, DocumentSharingPolicyDto policy)
        => await ReadOrThrow<DocumentSharingPolicyDto>(await _http.PutAsJsonAsync($"api/v1/organizations/{organizationId}/document-sharing/policy", policy, _json));

    // ---- Public viewer ----

    public async Task<SharedDocumentGateDto?> GetGateAsync(string slug)
    {
        var response = await _http.GetAsync($"api/v1/public/shares/{Uri.EscapeDataString(slug)}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadOrThrow<SharedDocumentGateDto>(response);
    }

    /// <summary>A POST that carries the browser's address and agent as X-Viewer-* headers, which the API prefers over the connection's own.</summary>
    private Task<HttpResponseMessage> PostAsViewer<T>(string path, T body, ViewerRequestInfo? viewer)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: _json) };
        if (!string.IsNullOrWhiteSpace(viewer?.IpAddress)) message.Headers.TryAddWithoutValidation("X-Viewer-Ip", viewer.IpAddress);
        if (!string.IsNullOrWhiteSpace(viewer?.IpAddress)) message.Headers.TryAddWithoutValidation("X-Real-IP", viewer.IpAddress);
        if (!string.IsNullOrWhiteSpace(viewer?.UserAgent)) message.Headers.TryAddWithoutValidation("X-Viewer-Agent", viewer.UserAgent);
        return _http.SendAsync(message);
    }

    public async Task<OpenSharedDocumentResponse> OpenAsync(string slug, OpenSharedDocumentRequest request, ViewerRequestInfo? viewer = null)
        => await ReadOrThrow<OpenSharedDocumentResponse>(await PostAsViewer($"api/v1/public/shares/{Uri.EscapeDataString(slug)}/open", request, viewer));

    public async Task<OpenSharedDocumentResponse> ResumeAsync(string slug, string sessionToken, ViewerRequestInfo? viewer = null)
        => await ReadOrThrow<OpenSharedDocumentResponse>(await PostAsViewer($"api/v1/public/shares/{Uri.EscapeDataString(slug)}/resume", new ResumeSharedDocumentRequest { SessionToken = sessionToken }, viewer));

    public async Task RecordPageAsync(string slug, SharedDocumentEventRequest request, ViewerRequestInfo? viewer = null)
    {
        try
        {
            await PostAsViewer($"api/v1/public/shares/{Uri.EscapeDataString(slug)}/events", request, viewer);
        }
        catch (Exception ex)
        {
            // A page-view that failed to log must never interrupt a reader.
            _logger.LogDebug(ex, "Share page-view event was not recorded");
        }
    }

    /// <summary>
    /// ApiPublicUrl, not Http.BaseAddress: the latter is ApiBaseUrl, the internal loopback in
    /// production, and the browser is the one doing this fetch. The same rule that fixed signage
    /// PDFs and welfare evidence uploads.
    /// </summary>
    private Uri PublicApi => new(_configuration["ApiPublicUrl"] ?? _http.BaseAddress!.ToString());

    public string ContentUrl(string slug, string contentToken)
        => new Uri(PublicApi, $"/api/v1/public/shares/{Uri.EscapeDataString(slug)}/content?t={Uri.EscapeDataString(contentToken)}").ToString();

    public string DownloadUrl(string slug, string sessionToken)
        => new Uri(PublicApi, $"/api/v1/public/shares/{Uri.EscapeDataString(slug)}/download?t={Uri.EscapeDataString(sessionToken)}").ToString();
}
