using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Application.DTOs;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Marketing;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public class ContactsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;

    public ContactsController(QMgrDbContext context, ITenantContextAccessor tenantAccessor)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
    }

    private static ContactDto MapToDto(Contact c) => new()
    {
        Id = c.Id,
        FullName = c.FullName,
        Phone = c.Phone,
        Email = c.Email,
        Tags = c.Tags,
        Source = c.Source,
        OptedOut = c.OptedOut,
        OptedOutAt = c.OptedOutAt,
        CreatedAt = c.CreatedAt
    };

    [HttpGet("marketing/contacts")]
    [Authorize]
    [RequirePermission(Permissions.MarketingView)]
    [ProducesResponseType(typeof(List<ContactDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetContacts([FromQuery] string? tag = null, [FromQuery] bool includeOptedOut = true)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var query = _context.Contacts.Where(c => c.OrganizationId == tenantContext.OrganizationId);

        if (!includeOptedOut)
            query = query.Where(c => !c.OptedOut);

        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(c => c.Tags != null && c.Tags.Contains(tag));

        var contacts = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
        return Ok(contacts.Select(MapToDto).ToList());
    }

    [HttpPost("marketing/contacts")]
    [Authorize]
    [RequirePermission(Permissions.MarketingManage)]
    [ProducesResponseType(typeof(ContactDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateContact([FromBody] CreateContactRequest request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        if (string.IsNullOrWhiteSpace(request.Phone) && string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new ProblemDetails { Title = "Phone or email is required", Status = StatusCodes.Status400BadRequest });

        var contact = new Contact
        {
            OrganizationId = tenantContext.OrganizationId,
            FullName = request.FullName,
            Phone = request.Phone,
            Email = request.Email,
            Tags = request.Tags
        };
        _context.Contacts.Add(contact);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetContacts), MapToDto(contact));
    }

    [HttpDelete("marketing/contacts/{contactId:guid}")]
    [Authorize]
    [RequirePermission(Permissions.MarketingManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteContact(Guid contactId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized();

        var contact = await _context.Contacts
            .FirstOrDefaultAsync(c => c.Id == contactId && c.OrganizationId == tenantContext.OrganizationId);
        if (contact == null) return NotFound();

        _context.Contacts.Remove(contact);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Public, unauthenticated one-click unsubscribe — the non-negotiable compliance mechanism
    /// this codebase had zero of before (see the earlier assessment). Looked up by an
    /// unguessable per-contact token, not by contact ID, so no auth/ownership check is needed:
    /// possessing the token (from a link in an actual message sent to this contact) is the proof.
    /// </summary>
    [HttpPost("marketing/unsubscribe/{token:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unsubscribe(Guid token)
    {
        var affected = await _context.Contacts
            .Where(c => c.OptOutToken == token && !c.OptedOut)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.OptedOut, true)
                .SetProperty(c => c.OptedOutAt, DateTime.UtcNow)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            // Already opted out, or an unknown token — either way, from the requester's
            // perspective the end state ("not receiving messages") is already true. Reporting
            // this as a plain success avoids leaking whether a token exists.
            var exists = await _context.Contacts.AnyAsync(c => c.OptOutToken == token);
            if (!exists) return NotFound();
        }

        return Ok(new { message = "You have been unsubscribed and will no longer receive messages." });
    }
}
