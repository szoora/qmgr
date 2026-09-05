using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.Commands.Queue;
using QMgr.Application.DTOs;
using QMgr.Application.Queries.Queue;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/branches/{branchId:guid}/tokens")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action already has its own [RequirePermission], this guards any future action that forgets one
[RequireModule(ModuleCodes.CoreQueue)]
public class TokensController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly QMgrDbContext _dbContext;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<TokensController> _logger;

    public TokensController(
        IMediator mediator,
        QMgrDbContext dbContext,
        ITenantContextAccessor tenantAccessor,
        ILogger<TokensController> logger)
    {
        _mediator = mediator;
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new queue token
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.TokensCreate)]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateToken(Guid branchId, [FromBody] CreateTokenRequest request)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var command = new CreateTokenCommand
        {
            BranchId = branchId,
            ServiceTypeCode = request.ServiceTypeCode,
            Customer = request.Customer,
            Source = request.Source,
            Priority = request.Priority,
            ExternalReference = request.ExternalReference,
            ExternalSystem = request.ExternalSystem,
            Metadata = request.Metadata,
            EstimatedArrival = request.EstimatedArrival
        };

        var result = await _mediator.Send(command);

        _logger.LogInformation("Token {TokenId} created for branch {BranchId}", result.Id, branchId);

        return CreatedAtAction(nameof(GetToken), new { branchId, tokenId = result.Id }, result);
    }

    /// <summary>
    /// Creates multiple queue tokens in bulk
    /// </summary>
    [HttpPost("bulk")]
    [RequirePermission(Permissions.TokensCreate)]
    [ProducesResponseType(typeof(List<TokenDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateTokensBulk(Guid branchId, [FromBody] CreateTokenBulkRequest request)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var commands = request.Tokens.Select(t => new CreateTokenCommand
        {
            BranchId = branchId,
            ServiceTypeCode = t.ServiceTypeCode,
            Customer = t.Customer,
            Source = t.Source,
            Priority = t.Priority,
            ExternalReference = t.ExternalReference,
            ExternalSystem = t.ExternalSystem,
            Metadata = t.Metadata,
            EstimatedArrival = t.EstimatedArrival
        }).ToList();

        var results = new List<TokenDto>();
        foreach (var command in commands)
        {
            var result = await _mediator.Send(command);
            results.Add(result);
        }

        return CreatedAtAction(nameof(GetWaitingTokens), new { branchId }, results);
    }

    /// <summary>
    /// Gets a specific token by ID
    /// </summary>
    [HttpGet("{tokenId:guid}")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetToken(Guid branchId, Guid tokenId)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        // SECURITY: BranchId is passed into the query and enforced in the handler too
        // (defense in depth) — a token ID is a global GUID, so without this check any user
        // with TokensView in their own org could read another tenant's token (and its
        // customer PII) by guessing/enumerating a tokenId while supplying their own branchId.
        var result = await _mediator.Send(new GetTokenQuery { TokenId = tokenId, BranchId = branchId });

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Gets token by external reference
    /// </summary>
    [HttpGet("by-reference")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTokenByExternalReference(
        Guid branchId,
        [FromQuery] string externalSystem,
        [FromQuery] string externalReference)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var result = await _mediator.Send(new GetTokenByExternalReferenceQuery
        {
            BranchId = branchId,
            ExternalSystem = externalSystem,
            ExternalReference = externalReference
        });

        if (result == null)
            return NotFound();

        return Ok(result);
    }

    /// <summary>
    /// Gets tokens by customer ID
    /// </summary>
    [HttpGet("by-customer/{customerId}")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(List<TokenDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTokensByCustomer(
        Guid branchId,
        string customerId,
        [FromQuery] bool activeOnly = true)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var result = await _mediator.Send(new GetTokensByCustomerQuery
        {
            BranchId = branchId,
            CustomerId = customerId,
            ActiveOnly = activeOnly
        });

        return Ok(result);
    }

    /// <summary>
    /// Gets all waiting tokens for a branch
    /// </summary>
    [HttpGet("waiting")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(List<TokenDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWaitingTokens(
        Guid branchId,
        [FromQuery] Guid? serviceTypeId = null,
        [FromQuery] int? limit = null)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var result = await _mediator.Send(new GetWaitingTokensQuery
        {
            BranchId = branchId,
            ServiceTypeId = serviceTypeId,
            Limit = limit
        });

        return Ok(result);
    }

    /// <summary>
    /// Updates a token's notes and integration metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was a stub until 2026-09-05: it verified the branch, fetched the token and returned it
    /// unchanged under a comment reading "Update logic here", so every caller got a 200 and no
    /// write. Nothing in this repository called it, but it is part of the public integration
    /// surface, where a silent no-op is worse than a 501.
    /// </para>
    /// <para>
    /// PATCH semantics: a field the caller omits is left alone. <c>Notes</c> sent as an empty
    /// string clears it, which is the only way to distinguish "clear this" from "don't touch it"
    /// when the field is a plain nullable string.
    /// </para>
    /// <para>
    /// <c>Metadata</c> <b>merges</b> rather than replaces, and a key sent with a null value is
    /// removed. That is not a style preference: <see cref="QueueController.SmsCountMetadataKey"/>
    /// lives in this same blob and caps how many times one ticket may be texted, so a wholesale
    /// replace would let any caller reset that cap by writing an unrelated key. The same key is
    /// refused outright below for the same reason.
    /// </para>
    /// <para>
    /// Gated on <c>tokens.create</c>, not <c>tokens.view</c>. It carried the view permission while
    /// it did nothing, which was harmless only for as long as that was true — a mutating endpoint
    /// behind a read permission is the actual defect here. There is no <c>tokens.edit</c>, and
    /// adding one would mean re-seeding permissions and re-granting five roles for no behavioural
    /// difference (the reasoning AppointmentsController already records for the same choice), so
    /// whoever may issue a ticket may annotate one.
    /// </para>
    /// </remarks>
    [HttpPatch("{tokenId:guid}")]
    [RequirePermission(Permissions.TokensCreate)]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateToken(
        Guid branchId,
        Guid tokenId,
        [FromBody] UpdateTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        if (request == null)
            return BadRequest(new ProblemDetails { Title = "A request body is required.", Status = StatusCodes.Status400BadRequest });

        if (request.Notes is { Length: > MaxNotesLength })
            return BadRequest(new ProblemDetails { Title = $"Notes must be {MaxNotesLength} characters or fewer.", Status = StatusCodes.Status400BadRequest });

        if (request.Metadata != null)
        {
            if (request.Metadata.Count > MaxMetadataKeys)
                return BadRequest(new ProblemDetails { Title = $"At most {MaxMetadataKeys} metadata keys can be set at once.", Status = StatusCodes.Status400BadRequest });

            var reserved = request.Metadata.Keys.FirstOrDefault(k =>
                string.Equals(k, QueueController.SmsCountMetadataKey, StringComparison.OrdinalIgnoreCase));
            if (reserved != null)
                return BadRequest(new ProblemDetails { Title = $"'{reserved}' is reserved and can't be set.", Status = StatusCodes.Status400BadRequest });
        }

        // SECURITY: BranchId in the predicate, not just the route — a token id is a global GUID,
        // so without it any caller could edit another tenant's token by supplying their own
        // branchId. Same guard GetToken above documents.
        var token = await _dbContext.Tokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId, cancellationToken);
        if (token == null)
            return NotFound();

        var changed = false;

        if (request.Notes != null)
        {
            var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
            if (token.Notes != notes)
            {
                token.Notes = notes;
                changed = true;
            }
        }

        if (request.Metadata is { Count: > 0 })
        {
            token.Metadata = MergeMetadata(token.Metadata, request.Metadata);
            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Token {TokenId} updated on branch {BranchId}", tokenId, branchId);
        }

        // Re-read through the query so the response is the same TokenDto shape every other read
        // here returns, rather than a second hand-written mapping of the same entity.
        var dto = await _mediator.Send(new GetTokenQuery { TokenId = tokenId, BranchId = branchId });
        return dto == null ? NotFound() : Ok(dto);
    }

    private const int MaxNotesLength = 1000;
    private const int MaxMetadataKeys = 50;

    /// <summary>
    /// Merges the supplied keys into the token's existing metadata. A key whose value is null is
    /// removed; every other key is set. Unparseable existing metadata is replaced rather than
    /// allowed to fail the request — it is an opaque integration blob this app does not own, and
    /// refusing every future write because of one bad row helps nobody.
    /// </summary>
    private static string MergeMetadata(string? existingJson, Dictionary<string, object> updates)
    {
        Dictionary<string, object> merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(existingJson)
                ? new Dictionary<string, object>()
                : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson) ?? new())
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        catch (JsonException)
        {
            merged = new Dictionary<string, object>();
        }

        foreach (var (key, value) in updates)
        {
            if (value is null) merged.Remove(key);
            else merged[key] = value;
        }

        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// Cancels a token
    /// </summary>
    [HttpPost("{tokenId:guid}/cancel")]
    [RequirePermission(Permissions.TokensCancel)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelToken(
        Guid branchId,
        Guid tokenId,
        [FromBody] CancelTokenRequest request)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        // SECURITY: BranchId is passed into the command and enforced in the handler — a
        // foreign tokenId can't be cancelled via a branchId the caller does legitimately own.
        var result = await _mediator.Send(new CancelTokenCommand
        {
            TokenId = tokenId,
            BranchId = branchId,
            Reason = request.Reason,
            CancelledBy = request.CancelledBy
        });

        if (!result)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Verifies that the branch belongs to the current organization
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        // SECURITY/CORRECTNESS: SuperAdmin's JWT carries the Platform org's own org_id (a
        // known quirk documented elsewhere in this codebase, e.g. QMgrDbContext.TenantIsolationEnabled),
        // so without this bypass SuperAdmin would be incorrectly blocked from every branch
        // outside the Platform org — matches the same bypass already present in
        // ContentController.VerifyBranchOwnership and OrganizationsController.VerifyOrganizationOwnership.
        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        var branchExists = await _dbContext.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null; // No error
    }
}

// Request DTOs
public record CreateTokenRequest
{
    public string ServiceTypeCode { get; init; } = string.Empty;
    public CustomerDto? Customer { get; init; }
    public QMgr.Domain.Enums.TokenSource Source { get; init; } = QMgr.Domain.Enums.TokenSource.API;
    public QMgr.Domain.Enums.TokenPriority Priority { get; init; } = QMgr.Domain.Enums.TokenPriority.Normal;
    public string? ExternalReference { get; init; }
    public string? ExternalSystem { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
    public DateTime? EstimatedArrival { get; init; }
}

public record CreateTokenBulkRequest
{
    public List<CreateTokenRequest> Tokens { get; init; } = new();
}

public record UpdateTokenRequest
{
    public Dictionary<string, object>? Metadata { get; init; }
    public string? Notes { get; init; }
}

public record CancelTokenRequest
{
    public string? Reason { get; init; }
    public string? CancelledBy { get; init; }
}
