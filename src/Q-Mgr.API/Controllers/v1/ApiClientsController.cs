using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.API.Authorization;
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
/// </summary>
[ApiController]
[Route("api/v1/api-clients")]
[Authorize]
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
            .Select(MapToDto);
        return Ok(dtos);
    }

    [HttpPost]
    [RequirePermission(Permissions.ApiClientsCreate)]
    public async Task<IActionResult> CreateApiClient([FromBody] CreateApiClientRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var clientId = $"qmgr_{Guid.NewGuid():N}"[..21];
        var clientSecret = GenerateClientSecret();

        var client = new ApiClient
        {
            OrganizationId = OrganizationId,
            Name = request.Name,
            Description = request.Description,
            SystemType = request.SystemType,
            ClientId = clientId,
            ClientSecretHash = BCrypt.Net.BCrypt.HashPassword(clientSecret),
            Scopes = request.Scopes.ToArray(),
            IsActive = request.IsActive
        };

        await _unitOfWork.ApiClients.AddAsync(client);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Created API client {ClientId} ({Name}) for organization {OrganizationId}",
            client.ClientId, client.Name, OrganizationId);

        var dto = new ApiClientSecretRevealDto { Client = MapToDto(client), ClientSecret = clientSecret };
        return CreatedAtAction(nameof(GetApiClients), new { id = client.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.ApiClientsEdit)]
    public async Task<IActionResult> UpdateApiClient(Guid id, [FromBody] UpdateApiClientRequest request)
    {
        var client = await _unitOfWork.ApiClients.FirstOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrganizationId);
        if (client == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        client.Name = request.Name;
        client.Description = request.Description;
        client.SystemType = request.SystemType;
        client.Scopes = request.Scopes.ToArray();
        client.IsActive = request.IsActive;

        await _unitOfWork.ApiClients.UpdateAsync(client);
        await _unitOfWork.SaveChangesAsync();

        return Ok(MapToDto(client));
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

    private static string GenerateClientSecret() => $"qmgr_sk_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";

    private static ApiClientDto MapToDto(ApiClient client) => new()
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
        CreatedAt = client.CreatedAt
    };
}
