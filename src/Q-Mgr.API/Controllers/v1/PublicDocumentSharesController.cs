using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;
using QMgr.Infrastructure.Services.Storage;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// The viewer's side of a share link: what /s/{slug} on the Web app talks to. Anonymous by
/// nature — the whole point is a reader with no login.
///
/// Every request re-evaluates the link's state from the row (never a cache), so revocation is
/// immediate. Refusals say as little as possible: an unknown slug and a revoked one both read
/// as "this link is not available", because confirming which is a small gift to whoever is
/// probing.
///
/// Status codes are chosen to stay clear of the Web app's own handlers: a 403 there is treated
/// as a tenant-status redirect, so a gate failure is a 200 with a status in the body, and a
/// content fetch with a bad token is a 401.
/// </summary>
[ApiController]
[Route("api/v1/public/shares")]
[AllowAnonymous]
public class PublicDocumentSharesController : ControllerBase
{
    private readonly QMgrDbContext _db;
    private readonly IDocumentShareService _shares;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PublicDocumentSharesController> _logger;

    public PublicDocumentSharesController(
        QMgrDbContext db,
        IDocumentShareService shares,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<PublicDocumentSharesController> logger)
    {
        _db = db;
        _shares = shares;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    private ShareViewerContext Viewer(string? email = null, Guid? sessionId = null)
    {
        // These calls come from the Blazor WEB SERVER's HttpClient, never from the reader's browser
        // — so the connection's own address is the Web box (the loopback in production) and there
        // is no browser user agent at all. The Web app relays the reader's real ones as
        // X-Viewer-Ip / X-Viewer-Agent (see ViewerRequestInfo on the Web side); they win when
        // present. Behind nginx a direct caller's address is X-Forwarded-For; locally it is the
        // connection's. A caller who forges the relay headers only mis-attributes their own rows.
        var relayedIp = Request.Headers["X-Viewer-Ip"].FirstOrDefault()?.Trim();
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
        var ip = !string.IsNullOrWhiteSpace(relayedIp) ? relayedIp
            : !string.IsNullOrWhiteSpace(forwarded) ? forwarded
            : HttpContext.Connection.RemoteIpAddress?.ToString();

        var relayedAgent = Request.Headers["X-Viewer-Agent"].FirstOrDefault();
        var agent = string.IsNullOrWhiteSpace(relayedAgent) ? Request.Headers.UserAgent.ToString() : relayedAgent;

        return new ShareViewerContext(ip, agent, email, sessionId);
    }

    private static string StateMessage(DocumentShareState state) => state switch
    {
        DocumentShareState.NotYetOpen => "This link is not open yet.",
        DocumentShareState.Expired => "This link has expired.",
        DocumentShareState.ViewLimitReached => "This link has reached its view limit.",
        DocumentShareState.Locked => "Too many failed attempts. Try again in a few minutes.",
        DocumentShareState.Revoked => "This link is no longer available.",
        DocumentShareState.Unavailable => "This link is no longer available.",
        _ => ""
    };

    // ---- 1. What is behind this link? --------------------------------------------------------------

    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(SharedDocumentGateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGate(string slug)
    {
        var share = await _shares.FindBySlugAsync(slug);
        if (share == null) return NotFound(new ProblemDetails { Title = "This link is not available", Status = StatusCodes.Status404NotFound });

        var state = _shares.EvaluateState(share, DateTime.UtcNow);
        var doc = share.MediaContent!;
        var orgName = await _db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == doc.OrganizationId).Select(o => o.Name).FirstOrDefaultAsync();

        // A link that cannot be opened reveals nothing about the document but the refusal.
        var open = state == DocumentShareState.Active;
        return Ok(new SharedDocumentGateDto
        {
            DocumentName = open ? doc.Name : "Shared document",
            Summary = open ? doc.Summary : null,
            OrganizationName = orgName,
            PublishedFrom = open ? doc.PublishedFrom : null,
            PublishedAt = open ? doc.PublishedAt : null,
            State = state,
            RequiresPasscode = share.RequiresPasscode,
            RequiresEmail = share.RequiresEmailVerification,
            AllowDownload = share.AllowDownload,
            ExpiresAt = open ? share.ExpiresAt : null,
            NotBefore = state == DocumentShareState.NotYetOpen ? share.NotBefore : null,
            Message = StateMessage(state)
        });
    }

    // ---- 2. Clear the gates --------------------------------------------------------------------------

    /// <summary>
    /// One endpoint, called as many times as the gates need: first with nothing (to learn what is
    /// required), then with the passcode, then with an email (a code is sent), then with the code
    /// and its challenge. Every step re-checks everything before it, so a client cannot skip one.
    /// </summary>
    [HttpPost("{slug}/open")]
    [ProducesResponseType(typeof(OpenSharedDocumentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Open(string slug, [FromBody] OpenSharedDocumentRequest? request)
    {
        request ??= new OpenSharedDocumentRequest();
        var share = await _shares.FindBySlugAsync(slug);
        if (share == null) return NotFound(new ProblemDetails { Title = "This link is not available", Status = StatusCodes.Status404NotFound });

        var now = DateTime.UtcNow;
        var state = _shares.EvaluateState(share, now);
        if (state != DocumentShareState.Active)
        {
            if (state != DocumentShareState.Locked)
                await _shares.RecordAsync(share, DocumentShareEventType.Denied, false, state.ToString(), Viewer());
            return Ok(new OpenSharedDocumentResponse
            {
                Status = state == DocumentShareState.Locked ? SharedDocumentOpenStatus.Locked : SharedDocumentOpenStatus.Denied,
                Message = StateMessage(state)
            });
        }

        // Gate 1: passcode.
        if (share.RequiresPasscode)
        {
            if (string.IsNullOrEmpty(request.Passcode))
                return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.PasscodeRequired });

            if (!await _shares.TryPasscodeAsync(share, request.Passcode, Viewer()))
            {
                var nowLocked = _shares.EvaluateState(share, DateTime.UtcNow) == DocumentShareState.Locked;
                return Ok(new OpenSharedDocumentResponse
                {
                    Status = nowLocked ? SharedDocumentOpenStatus.Locked : SharedDocumentOpenStatus.PasscodeInvalid,
                    Message = nowLocked ? StateMessage(DocumentShareState.Locked) : "That passcode is not right."
                });
            }
        }

        // Gate 2: email verification.
        string? verifiedEmail = null;
        if (share.RequiresEmailVerification)
        {
            if (!string.IsNullOrEmpty(request.Challenge) && !string.IsNullOrEmpty(request.Code))
            {
                verifiedEmail = await _shares.VerifyEmailChallengeAsync(share, request.Challenge, request.Code, Viewer());
                if (verifiedEmail == null)
                {
                    var nowLocked = _shares.EvaluateState(share, DateTime.UtcNow) == DocumentShareState.Locked;
                    return Ok(new OpenSharedDocumentResponse
                    {
                        Status = nowLocked ? SharedDocumentOpenStatus.Locked : SharedDocumentOpenStatus.CodeInvalid,
                        Message = nowLocked ? StateMessage(DocumentShareState.Locked) : "That code is not right, or it has expired."
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.Email))
            {
                var email = request.Email.Trim().ToLowerInvariant();

                // Not an address at all: refused without writing it to the log. The log's Email
                // column is exported to CSV by staff, and the security review found this branch
                // recorded any text a stranger typed — including a spreadsheet formula.
                if (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
                    return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.EmailRequired, Message = "Enter a valid email address." });

                if (!_shares.IsEmailAllowed(share, email))
                {
                    // The same wording as a wrong code — confirming an address is off the list
                    // would tell a stranger who IS on it, one guess at a time.
                    await _shares.RecordAsync(share, DocumentShareEventType.EmailRejected, false, "address not on the allow-list", Viewer(email));
                    return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.EmailNotAllowed, Message = "This link was not shared with that address." });
                }

                var challenge = await _shares.SendEmailChallengeAsync(share, email, Viewer());
                if (challenge == null)
                    return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.EmailRequired, Message = "We could not send a code to that address. Check it and try again." });

                return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.CodeSent, Challenge = challenge, Message = $"We emailed a code to {email}." });
            }
            else
            {
                return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.EmailRequired });
            }
        }

        // Every gate cleared. Count the open, and hand over the session and the sixty-second
        // content token.
        var grant = await _shares.OpenAsync(share, Viewer(), verifiedEmail);
        return Ok(Granted(share, grant));
    }

    /// <summary>After a page reload the session token gets a fresh content token without a new "open" — or a new passcode.</summary>
    [HttpPost("{slug}/resume")]
    [ProducesResponseType(typeof(OpenSharedDocumentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(string slug, [FromBody] ResumeSharedDocumentRequest request)
    {
        var share = await _shares.FindBySlugAsync(slug);
        if (share == null) return NotFound();

        var grant = _shares.ReadSessionToken(request.SessionToken);
        if (grant == null || grant.ShareId != share.Id)
            return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.Denied, Message = "Your session has ended. Open the link again." });

        // A resume is the SAME counted open (a reload), so the view limit does not apply to it —
        // see Serve for the same rule. Everything else still does.
        var state = _shares.EvaluateState(share, DateTime.UtcNow);
        if (state == DocumentShareState.ViewLimitReached) state = DocumentShareState.Active;
        if (state != DocumentShareState.Active)
        {
            await _shares.RecordAsync(share, DocumentShareEventType.Denied, false, state.ToString(), Viewer(grant.Email, grant.SessionId));
            return Ok(new OpenSharedDocumentResponse { Status = SharedDocumentOpenStatus.Denied, Message = StateMessage(state) });
        }

        return Ok(Granted(share, grant));
    }

    private OpenSharedDocumentResponse Granted(DocumentShare share, ShareGrant grant) => new()
    {
        Status = SharedDocumentOpenStatus.Granted,
        SessionToken = _shares.IssueSessionToken(grant),
        ContentToken = _shares.IssueContentToken(grant),
        ContentTokenSeconds = _shares.ContentTokenSeconds,
        AllowDownload = share.AllowDownload,
        WatermarkText = _shares.WatermarkText(share, grant.Email, DateTime.UtcNow),
        ViewerEmail = grant.Email,
        DocumentName = share.MediaContent!.Name,
        MimeType = share.MediaContent.MimeType ?? "application/pdf"
    };

    // ---- 3. The bytes ------------------------------------------------------------------------------

    /// <summary>Streams the document inline for the viewer. Needs the sixty-second content token.</summary>
    [HttpGet("{slug}/content")]
    public Task<IActionResult> ServeContent(string slug, [FromQuery(Name = "t")] string? token) => Serve(slug, token, download: false);

    /// <summary>The original file as an attachment — refused, and logged, when the link is view-only.</summary>
    [HttpGet("{slug}/download")]
    public Task<IActionResult> Download(string slug, [FromQuery(Name = "t")] string? token) => Serve(slug, token, download: true);

    private async Task<IActionResult> Serve(string slug, string? token, bool download)
    {
        var share = await _shares.FindBySlugAsync(slug);
        if (share == null) return NotFound();

        // A session token is accepted here too: the download button lives on a page that may
        // have been open for an hour, long after its content token died.
        var grant = _shares.ReadContentToken(token) ?? _shares.ReadSessionToken(token);
        if (grant == null || grant.ShareId != share.Id) return Unauthorized();

        // The view limit gates OPENS, not the bytes of a session that has already been counted:
        // a one-time link with download allowed counts its single view at Open and would then
        // refuse the download it was issued for (found by the e2e: 410 where 200 was expected).
        // Revocation, expiry and the window still apply — a grant is not a way past those.
        var state = _shares.EvaluateState(share, DateTime.UtcNow);
        if (state == DocumentShareState.ViewLimitReached) state = DocumentShareState.Active;
        if (state != DocumentShareState.Active)
        {
            await _shares.RecordAsync(share, DocumentShareEventType.Denied, false, state.ToString(), Viewer(grant.Email, grant.SessionId));
            return StatusCode(StatusCodes.Status410Gone, new ProblemDetails { Title = StateMessage(state), Status = StatusCodes.Status410Gone });
        }

        if (download && !share.AllowDownload)
        {
            // Withheld server-side. The page hides the button too, but the page is not the control.
            await _shares.RecordAsync(share, DocumentShareEventType.Denied, false, "download not allowed", Viewer(grant.Email, grant.SessionId));
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "This document is view-only", Status = StatusCodes.Status403Forbidden });
        }

        var doc = share.MediaContent!;
        var fileName = UploadAccessService.FileNameOf(doc.FileUrl) ?? Path.GetFileName(doc.FilePath ?? "");
        var store = LocalDiskMediaStorageService.ResolveStoreDirectory(_configuration, _environment);
        var diskPath = string.IsNullOrEmpty(fileName) ? null : Path.Combine(store, fileName);
        if (diskPath == null || !System.IO.File.Exists(diskPath))
        {
            _logger.LogError("Share {ShareId} points at a document whose file is missing: {FilePath}", share.Id, doc.FilePath);
            return StatusCode(StatusCodes.Status410Gone, new ProblemDetails { Title = "This document is no longer available", Status = StatusCodes.Status410Gone });
        }

        if (download)
            await _shares.RecordAsync(share, DocumentShareEventType.Downloaded, true, null, Viewer(grant.Email, grant.SessionId));

        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // Sharing is PDF-only (UpdatePublishing refuses anything else) and the upload path has
        // checked the bytes start with %PDF-, so the served type is a constant. It used to echo
        // doc.MimeType — the client's own declaration at upload — which the security review
        // showed could be text/html: a "PDF" that was really a script, rendered inline on this
        // origin for whoever opened the link.
        const string contentType = "application/pdf";
        var downloadName = (doc.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? doc.Name : doc.Name + ".pdf").Replace("\"", "");

        if (download)
            return PhysicalFile(diskPath, contentType, downloadName, enableRangeProcessing: true);

        Response.Headers[HeaderNames.ContentDisposition] = $"inline; filename=\"{downloadName}\"";
        return PhysicalFile(diskPath, contentType, enableRangeProcessing: true);
    }

    // ---- 4. Reading events -----------------------------------------------------------------------

    /// <summary>The viewer reports each page it showed and for how long. Session-token gated so a stranger cannot pollute a log.</summary>
    [HttpPost("{slug}/events")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RecordPage(string slug, [FromBody] SharedDocumentEventRequest request)
    {
        var share = await _shares.FindBySlugAsync(slug);
        if (share == null) return NotFound();

        var grant = _shares.ReadSessionToken(request.SessionToken);
        if (grant == null || grant.ShareId != share.Id) return Unauthorized();
        if (request.Page < 1 || request.Page > 10000) return BadRequest();

        await _shares.RecordAsync(share, DocumentShareEventType.PageViewed, true, null,
            Viewer(grant.Email, grant.SessionId), page: request.Page, dwellSeconds: Math.Clamp(request.DwellSeconds, 0, 24 * 3600));
        return NoContent();
    }
}
