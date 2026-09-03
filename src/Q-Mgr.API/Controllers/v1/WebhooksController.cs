using System.Security.Cryptography;
using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.Application.Commands.Queue;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Domain.Interfaces;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Inbound webhook receiver — the "other half" of the integration surface. WebhookService pushes
/// token events OUT to an API client's WebhookUrl; this endpoint lets that same client push
/// appointment events IN, authenticated with the same shared secret (HMAC-SHA256 over the raw
/// body, header <c>X-QMgr-Signature: sha256=&lt;hex&gt;</c>), so a partner only manages one key.
///
/// Deliberately [AllowAnonymous]: the caller is a server-to-server integration with no JWT and
/// no API key header — the signature IS the authentication. Every failure path before the
/// signature check returns a bare 401 so the endpoint doesn't leak which client ids exist.
/// </summary>
[ApiController]
[Route("api/v1/webhooks")]
[AllowAnonymous]
public class WebhooksController : ControllerBase
{
    /// <summary>Hard cap on the inbound body — appointment events are tiny; anything bigger is abuse.</summary>
    private const int MaxBodyBytes = 64 * 1024;
    private const string SignatureHeader = "X-QMgr-Signature";
    private const string SignaturePrefix = "sha256=";
    private const string DefaultExternalSystem = "webhook";
    private const string CancelReason = "Cancelled by integration";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMediator _mediator;
    private readonly IModuleAccessService _moduleAccessService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IUnitOfWork unitOfWork,
        IMediator mediator,
        IModuleAccessService moduleAccessService,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<WebhooksController> logger)
    {
        _unitOfWork = unitOfWork;
        _mediator = mediator;
        _moduleAccessService = moduleAccessService;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Receives one event from an external system on behalf of API client <paramref name="clientId"/>.
    /// Body: <c>{ "event": "appointment.created", "branchId": "guid", "externalReference": "...", "data": { ... } }</c>.
    /// Returns 202 <c>{ event, tokenId, displayNumber, status }</c>.
    /// </summary>
    [HttpPost("inbound/{clientId:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Inbound(Guid clientId, CancellationToken cancellationToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogInformation("Inbound webhook call for API client {ApiClientId} from {RemoteIp} ({ContentLength} bytes)",
            clientId, remoteIp, Request.ContentLength ?? -1);

        // ---- 1. Resolve the client. Anything short of "active with a secret" is a bare 401. ----
        var client = await _unitOfWork.ApiClients.GetByIdAsync(clientId, cancellationToken);
        if (client == null || !client.IsActive || string.IsNullOrEmpty(client.WebhookSecret))
        {
            _logger.LogWarning("Inbound webhook rejected for API client {ApiClientId}: {Reason}",
                clientId, client == null ? "unknown client" : !client.IsActive ? "client inactive" : "no webhook secret configured");
            return Unauthorized();
        }

        // ---- 2. Read the raw body (bounded) — the signature is over these exact bytes. ----
        if (Request.ContentLength > MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "PAYLOAD_TOO_LARGE", maxBytes = MaxBodyBytes });

        byte[] rawBody;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > MaxBodyBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "PAYLOAD_TOO_LARGE", maxBytes = MaxBodyBytes });
            rawBody = buffer.ToArray();
        }

        // ---- 3. Verify the HMAC before touching the payload at all. ----
        if (!VerifySignature(Request.Headers[SignatureHeader].ToString(), rawBody, client.WebhookSecret))
        {
            _logger.LogWarning("Inbound webhook rejected for API client {ApiClientId} ({ClientId}): bad or missing {Header}",
                clientId, client.ClientId, SignatureHeader);
            return Unauthorized();
        }

        // ---- 4. The client is genuine — from here on, errors can be descriptive. ----
        if (!await _moduleAccessService.IsModuleActiveAsync(client.OrganizationId, ModuleCodes.IntegrationsApi))
        {
            _logger.LogWarning("Inbound webhook refused for API client {ClientId}: organization {OrganizationId} lacks the {Module} module",
                client.ClientId, client.OrganizationId, ModuleCodes.IntegrationsApi);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "MODULE_INACTIVE", module = ModuleCodes.IntegrationsApi });
        }

        if (rawBody.Length == 0)
            return BadRequest(new { error = "EMPTY_BODY" });

        InboundEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<InboundEnvelope>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = "INVALID_JSON", detail = ex.Message });
        }

        if (envelope == null || string.IsNullOrWhiteSpace(envelope.Event))
            return BadRequest(new { error = "MISSING_EVENT", supported = SupportedEvents });

        var eventName = envelope.Event.Trim().ToLowerInvariant();
        if (!SupportedEvents.Contains(eventName))
            return BadRequest(new { error = "UNSUPPORTED_EVENT", supported = SupportedEvents });

        if (envelope.BranchId is not Guid branchId || branchId == Guid.Empty)
            return BadRequest(new { error = "MISSING_BRANCH", detail = "branchId is required." });

        // ---- 5. Branch must belong to the client's org and be within its AllowedBranches. ----
        var branch = await _unitOfWork.Branches.FirstOrDefaultAsync(
            b => b.Id == branchId && b.OrganizationId == client.OrganizationId, cancellationToken);
        if (branch == null)
            return NotFound(new { error = "BRANCH_NOT_FOUND", branchId });

        if (client.AllowedBranches is { Length: > 0 } && !client.AllowedBranches.Contains(branchId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "BRANCH_NOT_ALLOWED", branchId });

        // Resolve a tenant context for the rest of the request so downstream handlers behave as
        // they do for an authenticated call (EF tenant filters, usage metering in
        // CreateTokenCommandHandler). Nothing before this point relied on it.
        await ResolveTenantContextAsync(client.OrganizationId, cancellationToken);

        var externalSystem = string.IsNullOrWhiteSpace(client.SystemType) ? DefaultExternalSystem : client.SystemType.Trim();
        var externalReference = string.IsNullOrWhiteSpace(envelope.ExternalReference) ? null : envelope.ExternalReference.Trim();

        _logger.LogInformation("Inbound webhook {Event} for API client {ClientId} (org {OrganizationId}, branch {BranchId}, ref {ExternalReference})",
            eventName, client.ClientId, client.OrganizationId, branchId, externalReference ?? "-");

        return eventName switch
        {
            WebhookEventCatalog.AppointmentCreated => await HandleAppointmentCreatedAsync(client, branchId, externalSystem, externalReference, envelope.Data, cancellationToken),
            WebhookEventCatalog.AppointmentCancelled => await HandleAppointmentCancelledAsync(client, branchId, externalSystem, externalReference, cancellationToken),
            _ => BadRequest(new { error = "UNSUPPORTED_EVENT", supported = SupportedEvents })
        };
    }

    private async Task<IActionResult> HandleAppointmentCreatedAsync(
        ApiClient client, Guid branchId, string externalSystem, string? externalReference, JsonElement? dataElement, CancellationToken cancellationToken)
    {
        AppointmentData? data = null;
        if (dataElement is { ValueKind: JsonValueKind.Object } element)
        {
            try { data = element.Deserialize<AppointmentData>(JsonOptions); }
            catch (JsonException ex) { return BadRequest(new { error = "INVALID_DATA", detail = ex.Message }); }
        }
        data ??= new AppointmentData();

        // Service type: by id (preferred) or by code, always within the target branch.
        ServiceType? serviceType = null;
        if (data.ServiceTypeId is Guid serviceTypeId && serviceTypeId != Guid.Empty)
            serviceType = await _unitOfWork.ServiceTypes.FirstOrDefaultAsync(st => st.Id == serviceTypeId && st.BranchId == branchId, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(data.ServiceTypeCode))
        {
            var code = data.ServiceTypeCode.Trim();
            serviceType = await _unitOfWork.ServiceTypes.FirstOrDefaultAsync(st => st.Code == code && st.BranchId == branchId, cancellationToken);
        }
        else
            return BadRequest(new { error = "MISSING_SERVICE_TYPE", detail = "data.serviceTypeId (or data.serviceTypeCode) is required." });

        if (serviceType == null)
            return BadRequest(new { error = "SERVICE_TYPE_NOT_FOUND", detail = "No such service type in this branch." });

        // Idempotency: a re-delivered appointment.created for a reference that already has a live
        // token returns that token instead of queueing the customer twice.
        if (externalReference != null)
        {
            var existing = await _unitOfWork.Tokens.GetByExternalReferenceAsync(branchId, externalSystem, externalReference, cancellationToken);
            if (existing != null && existing.Status is not (TokenStatus.Completed or TokenStatus.Cancelled or TokenStatus.NoShow or TokenStatus.Transferred))
            {
                return Accepted(new
                {
                    @event = WebhookEventCatalog.AppointmentCreated,
                    tokenId = existing.Id,
                    displayNumber = existing.DisplayNumber,
                    status = "already_exists"
                });
            }
        }

        var hasCustomer = !string.IsNullOrWhiteSpace(data.CustomerId) || !string.IsNullOrWhiteSpace(data.CustomerName)
            || !string.IsNullOrWhiteSpace(data.CustomerPhone) || !string.IsNullOrWhiteSpace(data.CustomerEmail);

        var command = new CreateTokenCommand
        {
            BranchId = branchId,
            ServiceTypeCode = serviceType.Code,
            Customer = hasCustomer
                ? new CustomerDto { Id = data.CustomerId, Name = data.CustomerName, Phone = data.CustomerPhone, Email = data.CustomerEmail }
                : null,
            Source = TokenSource.Appointment,
            Priority = ParsePriority(data.Priority),
            ExternalReference = externalReference,
            ExternalSystem = externalSystem,
            EstimatedArrival = data.EstimatedArrival
        };

        TokenDto token;
        try
        {
            token = await _mediator.Send(command, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // CreateTokenCommandHandler's own validation (e.g. service type vanished between our lookup and its own).
            return BadRequest(new { error = "TOKEN_NOT_CREATED", detail = ex.Message });
        }

        _logger.LogInformation("Inbound webhook created token {TokenId} ({DisplayNumber}) for API client {ClientId}",
            token.Id, token.DisplayNumber, client.ClientId);

        return Accepted(new
        {
            @event = WebhookEventCatalog.AppointmentCreated,
            tokenId = token.Id,
            displayNumber = token.DisplayNumber,
            status = "created"
        });
    }

    private async Task<IActionResult> HandleAppointmentCancelledAsync(
        ApiClient client, Guid branchId, string externalSystem, string? externalReference, CancellationToken cancellationToken)
    {
        if (externalReference == null)
            return BadRequest(new { error = "MISSING_EXTERNAL_REFERENCE", detail = "externalReference is required to identify the token to cancel." });

        var token = await _unitOfWork.Tokens.GetByExternalReferenceAsync(branchId, externalSystem, externalReference, cancellationToken);
        if (token == null)
            return NotFound(new { error = "TOKEN_NOT_FOUND", externalReference, externalSystem });

        var cancelled = await _mediator.Send(new CancelTokenCommand
        {
            TokenId = token.Id,
            BranchId = branchId,
            Reason = CancelReason,
            CancelledBy = $"API client {client.Name}"
        }, cancellationToken);

        if (!cancelled)
        {
            return Conflict(new
            {
                error = "TOKEN_NOT_CANCELLABLE",
                tokenId = token.Id,
                status = token.Status.ToString().ToLowerInvariant(),
                detail = "The token is already in a terminal state."
            });
        }

        _logger.LogInformation("Inbound webhook cancelled token {TokenId} ({DisplayNumber}) for API client {ClientId}",
            token.Id, token.DisplayNumber, client.ClientId);

        return Accepted(new
        {
            @event = WebhookEventCatalog.AppointmentCancelled,
            tokenId = token.Id,
            displayNumber = token.DisplayNumber,
            status = "cancelled"
        });
    }

    private async Task ResolveTenantContextAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var current = _tenantContextAccessor.TenantContext;
        if (current?.IsResolved == true && current.OrganizationId == organizationId)
            return;

        var org = await _unitOfWork.Organizations.GetByIdAsync(organizationId, cancellationToken);
        if (org == null)
            return;

        _tenantContextAccessor.TenantContext = TenantContext.FromOrganization(
            org.Id, org.Slug, org.Tier, org.Status, org.SchemaName);
    }

    // ---------------------------------------------------------------------------------------
    // HMAC helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Expects <c>sha256=&lt;lowercase-or-uppercase hex&gt;</c>. Compares in constant time so a
    /// timing side-channel can't be used to forge a signature byte by byte.
    /// </summary>
    private static bool VerifySignature(string? headerValue, byte[] rawBody, string secret)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var value = headerValue.Trim();
        if (!value.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(value[SignaturePrefix.Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeSignature(rawBody, secret);
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static byte[] ComputeSignature(byte[] rawBody, string secret)
    {
        using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(rawBody);
    }

    private static TokenPriority ParsePriority(JsonElement? element)
    {
        if (element is not JsonElement value) return TokenPriority.Normal;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt32(out var number) && Enum.IsDefined(typeof(TokenPriority), number):
                return (TokenPriority)number;
            case JsonValueKind.String when Enum.TryParse<TokenPriority>(value.GetString(), ignoreCase: true, out var parsed):
                return parsed;
            default:
                return TokenPriority.Normal;
        }
    }

    private static readonly string[] SupportedEvents = WebhookEventCatalog.Inbound.Select(e => e.Name).ToArray();

    // ---------------------------------------------------------------------------------------
    // Wire shapes (private — the public contract is documented in docs/API_INTEGRATION_GUIDE.md)
    // ---------------------------------------------------------------------------------------

    private sealed record InboundEnvelope
    {
        public string? Event { get; init; }
        public Guid? BranchId { get; init; }
        public string? ExternalReference { get; init; }
        public JsonElement? Data { get; init; }
    }

    private sealed record AppointmentData
    {
        public Guid? ServiceTypeId { get; init; }
        public string? ServiceTypeCode { get; init; }
        public string? CustomerId { get; init; }
        public string? CustomerName { get; init; }
        public string? CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        /// <summary>"normal" | "priority" | "vip" | "emergency", or the enum's integer value.</summary>
        public JsonElement? Priority { get; init; }
        public DateTime? EstimatedArrival { get; init; }
    }
}
