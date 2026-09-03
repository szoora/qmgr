using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Interfaces;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Manages ApiClient records — the org-scoped credentials used by the real OAuth2
/// client-credentials flow already implemented in AuthController.GetToken (POST
/// api/v1/auth/token), verified there via BCrypt against ClientSecretHash. This controller
/// was previously missing entirely; the /admin/api-clients page had no backend to call and
/// showed 3 hardcoded fake clients instead.
///
/// Also owns each client's webhook configuration: the outbound subscription (WebhookUrl +
/// WebhookEvents, delivered by WebhookService) and the shared HMAC signing secret
/// (WebhookSecret), which signs outbound deliveries AND authenticates inbound calls to
/// WebhooksController (POST api/v1/webhooks/inbound/{clientId}).
/// </summary>
[ApiController]
[Route("api/v1/api-clients")]
[Authorize]
[RequireModule(ModuleCodes.IntegrationsApi)]
public class ApiClientsController : ControllerBase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<ApiClientsController> _logger;

    public ApiClientsController(
        IUnitOfWork unitOfWork,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<ApiClientsController> logger)
    {
        _unitOfWork = unitOfWork;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    private Guid OrganizationId => _tenantContextAccessor.TenantContext?.OrganizationId ?? Guid.Empty;

    [HttpGet]
    [RequirePermission(Permissions.ApiClientsView)]
    public async Task<IActionResult> GetApiClients()
    {
        var clients = await _unitOfWork.ApiClients.FindAsync(c => c.OrganizationId == OrganizationId);
        var dtos = clients
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => MapToDto(c));
        return Ok(dtos);
    }

    /// <summary>
    /// Canonical webhook event catalog — the outbound events a client can subscribe to and the
    /// inbound events it may POST to the receiver. Static; same for every organization.
    /// </summary>
    [HttpGet("webhook-events")]
    [RequirePermission(Permissions.ApiClientsView)]
    public IActionResult GetWebhookEvents() => Ok(WebhookEventCatalog.All);

    [HttpPost]
    [RequirePermission(Permissions.ApiClientsCreate)]
    public async Task<IActionResult> CreateApiClient([FromBody] CreateApiClientRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var webhookError = ValidateWebhookConfig(request.WebhookUrl, request.WebhookEvents, out var webhookUrl, out var webhookEvents);
        if (webhookError != null)
            return BadRequest(new { message = webhookError });

        var clientId = $"qmgr_{Guid.NewGuid():N}"[..21];
        var clientSecret = GenerateClientSecret();

        // A signing secret is generated whenever an outbound URL is configured, so the very
        // first delivery is already signed (and the same secret authenticates inbound calls).
        string? newWebhookSecret = webhookUrl != null ? GenerateWebhookSecret() : null;

        var client = new ApiClient
        {
            OrganizationId = OrganizationId,
            Name = request.Name,
            Description = request.Description,
            SystemType = request.SystemType,
            ClientId = clientId,
            ClientSecretHash = BCrypt.Net.BCrypt.HashPassword(clientSecret),
            Scopes = request.Scopes.ToArray(),
            IsActive = request.IsActive,
            WebhookUrl = webhookUrl,
            WebhookEvents = webhookEvents,
            WebhookSecret = newWebhookSecret
        };

        await _unitOfWork.ApiClients.AddAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Created API client {ClientId} ({Name}) for organization {OrganizationId}; webhook URL {HasWebhook}",
            client.ClientId, client.Name, OrganizationId, webhookUrl != null ? "configured" : "not configured");

        var dto = new ApiClientSecretRevealDto { Client = MapToDto(client, newWebhookSecret), ClientSecret = clientSecret };
        return CreatedAtAction(nameof(GetApiClients), new { id = client.Id }, dto);
    }

    /// <summary>
    /// Updates the client. If a webhook URL is set and the client has no signing secret yet, one
    /// is generated and returned once via <see cref="ApiClientDto.WebhookSecret"/> on this
    /// response only. Clearing the URL keeps the existing secret (it still authenticates
    /// inbound calls) — use DELETE or rotate if it must be invalidated.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.ApiClientsEdit)]
    public async Task<IActionResult> UpdateApiClient(Guid id, [FromBody] UpdateApiClientRequest request)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId);
        if (client == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var webhookError = ValidateWebhookConfig(request.WebhookUrl, request.WebhookEvents, out var webhookUrl, out var webhookEvents);
        if (webhookError != null)
            return BadRequest(new { message = webhookError });

        client.Name = request.Name;
        client.Description = request.Description;
        client.SystemType = request.SystemType;
        client.Scopes = request.Scopes.ToArray();
        client.IsActive = request.IsActive;
        client.WebhookUrl = webhookUrl;
        client.WebhookEvents = webhookEvents;

        string? newWebhookSecret = null;
        if (webhookUrl != null && string.IsNullOrEmpty(client.WebhookSecret))
        {
            newWebhookSecret = GenerateWebhookSecret();
            client.WebhookSecret = newWebhookSecret;
        }

        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        return Ok(MapToDto(client, newWebhookSecret));
    }

    /// <summary>
    /// Issues a new client secret and invalidates the old one immediately (only the hash is
    /// ever stored) — the plaintext secret is returned exactly once, here, and never again.
    /// </summary>
    [HttpPost("{id:guid}/regenerate-secret")]
    [RequirePermission(Permissions.ApiClientsEdit)]
    public async Task<IActionResult> RegenerateSecret(Guid id)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId);
        if (client == null)
            return NotFound();

        var newSecret = GenerateClientSecret();
        client.ClientSecretHash = BCrypt.Net.BCrypt.HashPassword(newSecret);

        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogWarning("Regenerated secret for API client {ClientId} in organization {OrganizationId}",
            client.ClientId, OrganizationId);

        return Ok(new ApiClientSecretRevealDto { Client = MapToDto(client), ClientSecret = newSecret });
    }

    /// <summary>
    /// Rotates (or creates, if none exists yet) the webhook signing secret. The old secret stops
    /// verifying immediately for both outbound signatures and inbound calls. The new secret is
    /// returned once via <see cref="ApiClientDto.WebhookSecret"/>. Works with no WebhookUrl
    /// configured — an inbound-only integration still needs a secret to sign its calls.
    /// </summary>
    [HttpPost("{id:guid}/webhook-secret/rotate")]
    [RequirePermission(Permissions.ApiClientsEdit)]
    public async Task<IActionResult> RotateWebhookSecret(Guid id)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId);
        if (client == null)
            return NotFound();

        var newSecret = GenerateWebhookSecret();
        client.WebhookSecret = newSecret;

        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogWarning("Rotated webhook secret for API client {ClientId} in organization {OrganizationId}",
            client.ClientId, OrganizationId);

        return Ok(MapToDto(client, newSecret));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.ApiClientsDelete)]
    public async Task<IActionResult> DeleteApiClient(Guid id)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId);
        if (client == null)
            return NotFound();

        await _unitOfWork.ApiClients.DeleteAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Deleted API client {ClientId} in organization {OrganizationId}", client.ClientId, OrganizationId);

        return NoContent();
    }

    /// <summary>
    /// Normalizes and validates the webhook URL + event list from a create/update request.
    /// Returns an error message, or null when valid. A blank URL means "no outbound webhook"
    /// (url = null, events = null). Only https is accepted, except plain http to localhost for
    /// local development. Event names must come from the outbound catalog (case-insensitive,
    /// stored canonical, de-duplicated).
    /// </summary>
    private static string? ValidateWebhookConfig(string? rawUrl, List<string>? rawEvents, out string? url, out string[]? events)
    {
        url = null;
        events = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return "Webhook URL must be an absolute URL (e.g. https://example.com/qmgr/webhook).";

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(isHttp && uri.IsLoopback))
            return "Webhook URL must use https (plain http is only allowed for localhost).";

        var canonical = new List<string>();
        foreach (var raw in rawEvents ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var match = WebhookEventCatalog.Outbound.FirstOrDefault(e => string.Equals(e.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return $"Unknown webhook event '{raw}'. Supported: {string.Join(", ", WebhookEventCatalog.Outbound.Select(e => e.Name))}.";
            if (!canonical.Contains(match.Name))
                canonical.Add(match.Name);
        }

        url = uri.ToString();
        events = canonical.ToArray();
        return null;
    }

    private static string GenerateClientSecret() => $"qmgr_sk_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";

    /// <summary>32 random bytes, Base64 — the shared HMAC-SHA256 key for this client's webhooks.</summary>
    private static string GenerateWebhookSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static ApiClientDto MapToDto(ApiClient client, string? revealWebhookSecret = null) => new()
    {
        Id = client.Id,
        Name = client.Name,
        Description = client.Description,
        ClientId = client.ClientId,
        SystemType = client.SystemType,
        IsActive = client.IsActive,
        Scopes = client.Scopes?.ToList() ?? new(),
        RateLimitPerMinute = client.RateLimitPerMinute,
        LastUsedAt = client.LastUsedAt,
        CreatedAt = client.CreatedAt,
        WebhookUrl = client.WebhookUrl,
        WebhookEvents = client.WebhookEvents?.ToList() ?? new(),
        HasWebhookSecret = !string.IsNullOrEmpty(client.WebhookSecret),
        WebhookSecret = revealWebhookSecret
    };
}
