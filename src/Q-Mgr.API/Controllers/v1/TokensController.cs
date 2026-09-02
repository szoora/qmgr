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
    /// Updates token metadata
    /// </summary>
    [HttpPatch("{tokenId:guid}")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(TokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateToken(
        Guid branchId,
        Guid tokenId,
        [FromBody] UpdateTokenRequest request)
    {
        // SECURITY: Verify branch belongs to organization
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        // Implementation for updating token metadata
        // SECURITY: same cross-tenant guard as GetToken above — BranchId enforced in the handler.
        var token = await _mediator.Send(new GetTokenQuery { TokenId = tokenId, BranchId = branchId });
        if (token == null)
            return NotFound();

        // Update logic here
        return Ok(token);
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
