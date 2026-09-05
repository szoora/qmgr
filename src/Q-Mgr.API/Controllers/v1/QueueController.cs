using System.Globalization;
using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.API.Authorization;
using QMgr.Application.Commands.Queue;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Interfaces.Billing;
using QMgr.Application.Queries.Queue;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

/// <summary>
/// Public queue endpoints for customer-facing displays, the unattended lobby kiosk and the
/// phone "join the queue" / "track my ticket" pages.
/// Note: These endpoints are intentionally open for public kiosk/display access.
/// For authenticated queue management, use the TokensController.
/// <para>
/// Module gating still applies: the whole <c>api/v1/branches/{branchId}/queue</c> prefix is
/// mapped to Core Queue Management in <see cref="ModuleRouteMap"/>, and
/// <c>ModuleAccessMiddleware</c> resolves the owning organization from the branch id in the
/// route precisely so anonymous endpoints like these can't be the way around the gate.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/branches/{branchId:guid}/queue")]
[Produces("application/json")]
[AllowAnonymous] // Public endpoints for customer kiosk/display - no authentication required
public class QueueController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly QMgrDbContext _context;
    private readonly IDistributedCache _cache;
    private readonly IUsageTrackingService _usageTracking;
    private readonly IQueueCustomerNotifier _customerNotifier;
    private readonly ILogger<QueueController> _logger;

    // Free-text caps for the anonymous ticket-issuing endpoint. Nothing here is trusted: an
    // unauthenticated POST must not be able to push arbitrarily long strings into the tokens
    // table. These sit at or below the column/validator limits CreateTokenCommandValidator
    // already enforces, so a request that passes here also passes validation.
    private const int MaxNameLength = 120;
    private const int MaxPhoneLength = 32;
    private const int MaxEmailLength = 160;
    private const int MaxServiceCodeLength = 10;
    private const int MaxDisplayNumberLength = 24;

    // Fixed-window abuse control. Two separate budgets: issuing a ticket is a write and is
    // scarce; reading a ticket's status is a poll from the customer's own phone and needs to
    // be generous enough for the status page's refresh timer.
    private static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(10);
    private const int IssueLimitPerWindow = 5;
    private static readonly TimeSpan LookupWindow = TimeSpan.FromMinutes(5);
    private const int LookupLimitPerWindow = 120;

    // Texting a ticket is the only thing here that spends the organization's money, so it gets a
    // third, tighter budget and two caps the other endpoints do not need: how many messages one
    // ticket may ever ask for, and how old a ticket may be and still ask. Without both, an
    // anonymous endpoint that sends SMS to a caller-supplied number is an SMS pump.
    private static readonly TimeSpan SmsWindow = TimeSpan.FromMinutes(10);
    private const int SmsLimitPerWindow = 3;
    private const int MaxSmsPerToken = 3;
    private static readonly TimeSpan SmsTicketMaxAge = TimeSpan.FromHours(4);
    private const string SmsCountMetadataKey = "ticketSmsCount";

    public QueueController(
        IMediator mediator,
        QMgrDbContext context,
        IDistributedCache cache,
        IUsageTrackingService usageTracking,
        IQueueCustomerNotifier customerNotifier,
        ILogger<QueueController> logger)
    {
        _mediator = mediator;
        _context = context;
        _cache = cache;
        _usageTracking = usageTracking;
        _customerNotifier = customerNotifier;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current queue status for a branch
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(QueueStatusDto), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetQueueStatus(Guid branchId)
    {
        var result = await _mediator.Send(new GetQueueStatusQuery { BranchId = branchId });
        return Ok(result);
    }

    /// <summary>
    /// Waiting tokens for a public display — the same query TokensController.GetWaitingTokens runs,
    /// but anonymous (a display screen has no session) and with every customer field stripped.
    /// The public board only ever shows ticket numbers and service names.
    /// </summary>
    [HttpGet("waiting")]
    [ProducesResponseType(typeof(List<TokenDto>), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 3)]
    public async Task<IActionResult> GetPublicWaitingTokens(Guid branchId, [FromQuery] Guid? serviceTypeId = null, [FromQuery] int? limit = null)
    {
        var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
        var result = await _mediator.Send(new GetWaitingTokensQuery
        {
            BranchId = branchId,
            ServiceTypeId = serviceTypeId,
            Limit = cappedLimit
        });

        var scrubbed = result.Select(t => t with
        {
            Customer = null,
            Notes = null,
            Metadata = null,
            ExternalReference = null,
            ExternalSystem = null
        }).ToList();

        return Ok(scrubbed);
    }

    /// <summary>
    /// Gets estimated wait time for a service type
    /// </summary>
    [HttpGet("wait-time/{serviceTypeId:guid}")]
    [ProducesResponseType(typeof(WaitTimeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWaitTime(Guid branchId, Guid serviceTypeId)
    {
        var status = await _mediator.Send(new GetQueueStatusQuery { BranchId = branchId });
        var serviceType = status.ServiceTypes.FirstOrDefault(st => st.Id == serviceTypeId);

        if (serviceType == null)
            return NotFound();

        return Ok(new WaitTimeResponse
        {
            ServiceTypeId = serviceTypeId,
            ServiceTypeName = serviceType.Name,
            WaitingCount = serviceType.WaitingCount,
            EstimatedWaitMinutes = serviceType.EstimatedWaitMinutes,
            CountersActive = serviceType.CountersActive
        });
    }

    /// <summary>
    /// The service menu a kiosk / join page needs, and nothing else: what a customer can pick,
    /// how it's labelled, and how busy it is. Exists so an unattended kiosk stops calling the
    /// authorized <c>GET api/v1/branches/{branchId}/service-types</c> (which 401s with no staff
    /// session on the device) just to render its tiles.
    /// </summary>
    [HttpGet("service-types")]
    [ProducesResponseType(typeof(List<PublicServiceTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetPublicServiceTypes(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await _context.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

        if (branch == null || !branch.IsActive)
            return BranchNotFound(branchId);

        var serviceTypes = await _context.ServiceTypes
            .AsNoTracking()
            .Where(st => st.BranchId == branchId && st.IsActive)
            .OrderByDescending(st => st.Priority)
            .ThenBy(st => st.Name)
            .ToListAsync(cancellationToken);

        var waitingCounts = await WaitingCountsByServiceTypeAsync(branchId, cancellationToken);

        var result = serviceTypes.Select(st =>
        {
            var waiting = waitingCounts.TryGetValue(st.Id, out var count) ? count : 0;
            return new PublicServiceTypeDto
            {
                Id = st.Id,
                Code = st.Code,
                Name = st.Name,
                Description = st.Description,
                IconUrl = st.IconUrl,
                Color = st.Color,
                WaitingCount = waiting,
                // Same arithmetic GetQueueStatusQueryHandler uses, so the kiosk tile and the
                // dashboard never quote two different numbers for the same queue.
                EstimatedWaitMinutes = st.AverageServiceTimeMinutes * waiting
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Issues a ticket with no authentication at all — the one thing a lobby kiosk (or a
    /// customer's phone at <c>/join/{branchId}</c>) has to be able to do unattended.
    /// <para>
    /// This is why it isn't just "call TokensController without [Authorize]": an anonymous write
    /// endpoint needs its own abuse controls. There are three, all enforced below — a
    /// per-branch-per-IP fixed-window cap (429 + Retry-After), hard length caps on every
    /// free-text field, and the branch's own MaxQueueSize from BranchSettings (409 when full).
    /// </para>
    /// </summary>
    [HttpPost("tokens")]
    [ProducesResponseType(typeof(PublicTicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> IssuePublicToken(
        Guid branchId,
        [FromBody] PublicJoinQueueRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest(new { error = "INVALID_REQUEST", message = "A request body is required." });

        if (request.ServiceTypeId is null && string.IsNullOrWhiteSpace(request.ServiceTypeCode))
            return BadRequest(new { error = "SERVICE_TYPE_REQUIRED", message = "Choose a service before requesting a ticket." });

        // Length caps first: cheapest check, and it runs before anything touches the database.
        var name = Trimmed(request.CustomerName);
        var phone = Trimmed(request.CustomerPhone);
        var email = Trimmed(request.CustomerEmail);
        var code = Trimmed(request.ServiceTypeCode);

        if (name?.Length > MaxNameLength)
            return BadRequest(new { error = "FIELD_TOO_LONG", message = $"Name must be {MaxNameLength} characters or fewer." });
        if (phone?.Length > MaxPhoneLength)
            return BadRequest(new { error = "FIELD_TOO_LONG", message = $"Phone number must be {MaxPhoneLength} characters or fewer." });
        if (email?.Length > MaxEmailLength)
            return BadRequest(new { error = "FIELD_TOO_LONG", message = $"Email must be {MaxEmailLength} characters or fewer." });
        if (code?.Length > MaxServiceCodeLength)
            return BadRequest(new { error = "FIELD_TOO_LONG", message = "Unknown service." });

        var branch = await _context.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

        if (branch == null || !branch.IsActive)
            return BranchNotFound(branchId);

        // The service type must belong to THIS branch and be active. Without the branch check a
        // caller could pass another tenant's service-type id and have a ticket issued against it.
        var candidates = _context.ServiceTypes
            .AsNoTracking()
            .Where(st => st.BranchId == branchId && st.IsActive);

        candidates = request.ServiceTypeId is { } wantedId
            ? candidates.Where(st => st.Id == wantedId)
            : candidates.Where(st => st.Code == code);

        var serviceType = await candidates.FirstOrDefaultAsync(cancellationToken);

        if (serviceType == null)
            return NotFound(new ProblemDetails
            {
                Title = "Service not available",
                Detail = "That service is not offered at this branch right now.",
                Status = StatusCodes.Status404NotFound
            });

        var rateLimit = await CheckRateLimitAsync(
            $"pubqueue:issue:{branchId}:{ClientKey()}", IssueLimitPerWindow, IssueWindow, cancellationToken);

        if (!rateLimit.Allowed)
        {
            Response.Headers.RetryAfter = rateLimit.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            _logger.LogWarning("Anonymous ticket issuing rate limit hit for branch {BranchId}", branchId);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "RATE_LIMITED",
                message = "Too many tickets requested from this device. Please wait a moment or ask a member of staff.",
                retryAfterSeconds = rateLimit.RetryAfterSeconds
            });
        }

        var maxQueueSize = await GetMaxQueueSizeAsync(branchId, cancellationToken);
        if (maxQueueSize > 0)
        {
            var waitingNow = await WaitingTokensQuery(branchId).CountAsync(cancellationToken);
            if (waitingNow >= maxQueueSize)
            {
                return Conflict(new
                {
                    error = "QUEUE_FULL",
                    message = "The queue is full at the moment. Please try again shortly or ask a member of staff.",
                    waitingCount = waitingNow,
                    maxQueueSize
                });
            }
        }

        var token = await _mediator.Send(new CreateTokenCommand
        {
            BranchId = branchId,
            ServiceTypeCode = serviceType.Code,
            // TokenSource has no dedicated "public/self-service" member; Kiosk is the closest
            // and is already this command's default for exactly this path.
            Source = TokenSource.Kiosk,
            Priority = TokenPriority.Normal,
            Customer = (name is null && phone is null && email is null)
                ? null
                : new CustomerDto { Name = name, Phone = phone, Email = email }
        }, cancellationToken);

        // CreateTokenCommandHandler meters usage off the resolved tenant context, which is null
        // for an anonymous caller — so a self-service ticket would otherwise never be counted.
        // The branch names its owning organization, so count it here instead.
        try
        {
            await _usageTracking.IncrementTokensCreatedAsync(branch.OrganizationId);
        }
        catch (Exception ex)
        {
            // Metering must never cost the customer their ticket.
            _logger.LogError(ex, "Failed to meter anonymous token creation for organization {OrganizationId}", branch.OrganizationId);
        }

        return Ok(new PublicTicketDto
        {
            TokenId = token.Id,
            DisplayNumber = token.DisplayNumber,
            ServiceTypeId = serviceType.Id,
            ServiceTypeName = serviceType.Name,
            Position = token.PositionInQueue ?? 0,
            EstimatedWaitMinutes = token.EstimatedWaitMinutes ?? 0,
            IssuedAt = token.CreatedAt
        });
    }

    /// <summary>
    /// Live status of one ticket for the customer's own phone (<c>/ticket/{branchId}/{number}</c>).
    /// <para>
    /// SECURITY: a display number is short and sequential, so this URL is guessable by anyone.
    /// It therefore returns only what the lobby display board already shows publicly — number,
    /// service, status, place in line, wait estimate, counter — and never the customer's name,
    /// phone, email, notes or external reference. Keep it that way if this response is ever
    /// extended.
    /// </para>
    /// </summary>
    [HttpGet("tokens/{displayNumber}")]
    [ProducesResponseType(typeof(PublicTicketStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetPublicTicketStatus(Guid branchId, string displayNumber, CancellationToken cancellationToken)
    {
        var number = Trimmed(displayNumber);
        if (number == null || number.Length > MaxDisplayNumberLength)
            return TicketNotFound();

        var rateLimit = await CheckRateLimitAsync(
            $"pubqueue:lookup:{branchId}:{ClientKey()}", LookupLimitPerWindow, LookupWindow, cancellationToken);

        if (!rateLimit.Allowed)
        {
            Response.Headers.RetryAfter = rateLimit.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "RATE_LIMITED",
                message = "Too many status checks from this device. Please wait a moment before trying again.",
                retryAfterSeconds = rateLimit.RetryAfterSeconds
            });
        }

        var since = DateTime.UtcNow.Date;

        // Display numbers restart each day, so scope to today and take the newest match.
        var token = await _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.DisplayNumber == number && t.CreatedAt >= since)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (token == null)
            return TicketNotFound();

        var serviceTypeName = await _context.ServiceTypes
            .AsNoTracking()
            .Where(st => st.Id == token.ServiceTypeId)
            .Select(st => st.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "General Service";

        var peopleAhead = 0;
        if (token.Status == TokenStatus.Waiting)
        {
            // Same ordering the queue itself uses (priority first, then arrival), so the number
            // shown on the phone matches the order people are actually called in.
            peopleAhead = await _context.Tokens
                .AsNoTracking()
                .CountAsync(t => t.BranchId == token.BranchId
                                 && t.ServiceTypeId == token.ServiceTypeId
                                 && t.Status == TokenStatus.Waiting
                                 && (t.Priority > token.Priority
                                     || (t.Priority == token.Priority && t.CreatedAt < token.CreatedAt)),
                    cancellationToken);
        }

        string? counterNumber = null;
        if (token.CounterId.HasValue && token.Status is TokenStatus.Called or TokenStatus.Serving)
        {
            counterNumber = await _context.Counters
                .AsNoTracking()
                .Where(c => c.Id == token.CounterId.Value)
                .Select(c => c.DisplayName ?? c.CounterNumber)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var averageServiceMinutes = await _context.ServiceTypes
            .AsNoTracking()
            .Where(st => st.Id == token.ServiceTypeId)
            .Select(st => (int?)st.AverageServiceTimeMinutes)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        return Ok(new PublicTicketStatusDto
        {
            DisplayNumber = token.DisplayNumber,
            Status = token.Status.ToString(),
            ServiceTypeName = serviceTypeName,
            Position = token.Status == TokenStatus.Waiting ? peopleAhead + 1 : 0,
            PeopleAhead = peopleAhead,
            EstimatedWaitMinutes = token.Status == TokenStatus.Waiting
                ? peopleAhead * averageServiceMinutes
                : 0,
            CounterNumber = counterNumber,
            IssuedAt = token.CreatedAt,
            ServerTime = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Texts a ticket's details to a phone number the customer gives at the kiosk, and stores that
    /// number on the ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the kiosk's "Send SMS" button does; until now it answered "coming soon". The
    /// phone step before a ticket is issued is optional and easy to skip on a lobby terminal, and
    /// somebody who skipped it had no second chance. Storing the number is the more useful half —
    /// it means the automatic "it's your turn" message reaches them too, not just this one
    /// confirmation.
    /// </para>
    /// <para>
    /// It lives here, on the anonymous controller, because the kiosk is unattended and issues its
    /// tickets through <see cref="IssuePublicToken"/> with no staff session — an authenticated
    /// endpoint would 401 on exactly the device that needs this. That makes it the only anonymous
    /// endpoint in this app that spends money, so it carries four controls rather than one:
    /// a tight per-device rate limit, a hard cap of <see cref="MaxSmsPerToken"/> messages for the
    /// life of any one ticket, a refusal once a ticket is older than
    /// <see cref="SmsTicketMaxAge"/>, and a refusal for a ticket that is already finished. An
    /// attacker who wants to pump SMS through this has to keep issuing real tickets against a real
    /// branch, through the issue endpoint's own rate limit, for three messages each.
    /// </para>
    /// </remarks>
    [HttpPost("tokens/{tokenId:guid}/send-sms")]
    [ProducesResponseType(typeof(SendTicketSmsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SendTicketSms(
        Guid branchId,
        Guid tokenId,
        [FromBody] SendTicketSmsRequest request,
        CancellationToken cancellationToken)
    {
        var phone = Trimmed(request?.PhoneNumber);

        // Deliberately loose on shape: this app serves Uganda and its neighbours, where a number
        // is written 0771…, +256771… and 256771… interchangeably. Rejecting on a pattern here
        // would refuse real numbers; the gateway is the thing that actually knows.
        if (string.IsNullOrWhiteSpace(phone) || phone.Length < 7 || phone.Length > MaxPhoneLength || !phone.Any(char.IsDigit))
            return BadRequest(new { error = "INVALID_PHONE", message = "Please enter a valid phone number." });

        var branch = await _context.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch == null || !branch.IsActive) return BranchNotFound(branchId);

        var rateLimit = await CheckRateLimitAsync(
            $"pubqueue:sms:{branchId}:{ClientKey()}", SmsLimitPerWindow, SmsWindow, cancellationToken);

        if (!rateLimit.Allowed)
        {
            Response.Headers.RetryAfter = rateLimit.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            _logger.LogWarning("Anonymous ticket-SMS rate limit hit for branch {BranchId}", branchId);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "RATE_LIMITED",
                message = "Too many messages requested from this device. Please wait a moment or ask a member of staff.",
                retryAfterSeconds = rateLimit.RetryAfterSeconds
            });
        }

        var token = await _context.Tokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId, cancellationToken);
        if (token == null)
            return NotFound(new ProblemDetails { Title = "That ticket no longer exists.", Status = StatusCodes.Status404NotFound });

        if (token.Status is not (TokenStatus.Waiting or TokenStatus.Called))
            return BadRequest(new { error = "TICKET_CLOSED", message = "That ticket has already been served." });

        if (DateTime.UtcNow - token.CreatedAt > SmsTicketMaxAge)
            return BadRequest(new { error = "TICKET_EXPIRED", message = "That ticket is too old to send." });

        var alreadySent = ReadSmsCount(token.Metadata);
        if (alreadySent >= MaxSmsPerToken)
            return BadRequest(new { error = "SMS_LIMIT", message = $"This ticket has already been texted {MaxSmsPerToken} times. Please note the number down." });

        // Counted before the send, not after: a gateway that times out after accepting the message
        // must not hand out a free retry, and over-counting one message is a far cheaper mistake
        // than under-counting an unbounded number of them.
        token.Metadata = WriteSmsCount(token.Metadata, alreadySent + 1);
        await _context.SaveChangesAsync(cancellationToken);

        var result = await _customerNotifier.SendTicketDetailsAsync(tokenId, branchId, phone, cancellationToken);

        var message = result.Outcome switch
        {
            TicketDetailsSendOutcome.Sent => "Sent — check your phone.",
            TicketDetailsSendOutcome.ChannelDisabled =>
                "Text messages are switched off here, so nothing was sent. Your number is saved against the ticket.",
            TicketDetailsSendOutcome.NotConfigured =>
                "No text-message service is set up here yet, so nothing was sent. Your number is saved against the ticket.",
            TicketDetailsSendOutcome.TokenNotFound => "That ticket no longer exists.",
            _ => "The message could not be delivered. Your number is saved against the ticket — please note your ticket number down as well."
        };

        _logger.LogInformation("Ticket {TokenId} SMS request from a kiosk: {Outcome}", tokenId, result.Outcome);

        return Ok(new SendTicketSmsResponse
        {
            Sent = result.Outcome == TicketDetailsSendOutcome.Sent,
            PhoneStored = result.PhoneStored,
            Outcome = result.Outcome.ToString(),
            Message = message
        });
    }

    #region Helpers

    /// <summary>
    /// How many times this ticket has been texted, kept in the existing Token.Metadata JSON rather
    /// than a new column — the standing enhance-before-you-add rule. A malformed blob reads as
    /// zero, which is the safe direction only because the per-device rate limit is the real
    /// backstop; this cap is the second line, not the first.
    /// </summary>
    private static int ReadSmsCount(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return 0;
        try
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
            if (root != null && root.TryGetValue(SmsCountMetadataKey, out var el) && el.TryGetInt32(out var count))
                return count;
        }
        catch (JsonException) { /* integration blob we don't own — treat as no sends */ }
        return 0;
    }

    /// <summary>Merges the counter back in, leaving any integration data in the blob alone.</summary>
    private static string WriteSmsCount(string? metadataJson, int count)
    {
        Dictionary<string, object> merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(metadataJson)
                ? new Dictionary<string, object>()
                : (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson) ?? new())
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        catch (JsonException)
        {
            // Unparseable metadata is not ours to repair, but it must not block the counter that
            // caps this endpoint — start a clean object rather than losing the cap entirely.
            merged = new Dictionary<string, object>();
        }

        merged[SmsCountMetadataKey] = count;
        return JsonSerializer.Serialize(merged);
    }

    private IActionResult BranchNotFound(Guid branchId) => NotFound(new ProblemDetails
    {
        Title = "Branch not found",
        Detail = $"Branch with ID '{branchId}' was not found or is not active.",
        Status = StatusCodes.Status404NotFound
    });

    private IActionResult TicketNotFound() => NotFound(new ProblemDetails
    {
        Title = "Ticket not found",
        Detail = "No ticket with that number was issued at this branch today.",
        Status = StatusCodes.Status404NotFound
    });

    private static string? Trimmed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private IQueryable<QMgr.Domain.Entities.Queue.Token> WaitingTokensQuery(Guid branchId)
    {
        var since = DateTime.UtcNow.Date;
        return _context.Tokens
            .AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status == TokenStatus.Waiting && t.CreatedAt >= since);
    }

    private async Task<Dictionary<Guid, int>> WaitingCountsByServiceTypeAsync(Guid branchId, CancellationToken cancellationToken)
    {
        return await WaitingTokensQuery(branchId)
            .GroupBy(t => t.ServiceTypeId)
            .Select(g => new { ServiceTypeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceTypeId, x => x.Count, cancellationToken);
    }

    /// <summary>
    /// The branch's configured queue cap, or 0 for "no cap". Lives inside
    /// BranchSettings.SystemSettingsJson (a serialized <see cref="SystemSettingsDto"/>), which is
    /// where the /admin/settings page writes it — a malformed or absent blob means no cap rather
    /// than a refused ticket.
    /// </summary>
    private async Task<int> GetMaxQueueSizeAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var json = await _context.BranchSettings
            .AsNoTracking()
            .Where(s => s.BranchId == branchId)
            .Select(s => s.SystemSettingsJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
            return 0;

        try
        {
            var settings = JsonSerializer.Deserialize<SystemSettingsDto>(json);
            return settings?.MaxQueueSize ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Caller identity for rate-limiting purposes.
    /// NOTE: this is the socket peer. Behind a reverse proxy that is the proxy, not the visitor,
    /// unless the API is configured with UseForwardedHeaders — see the deployment note in the
    /// handover for this change.
    /// </summary>
    private string ClientKey()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;
    }

    /// <summary>
    /// Fixed-window counter over IDistributedCache (the same cache ModulesController uses for
    /// its pending-purchase records). The window index is baked into the key so the entry
    /// expires with the window and there is no separate reset to run.
    /// <para>
    /// Read-modify-write is not atomic here, so two simultaneous requests can share a slot. That
    /// is acceptable for a lobby abuse control — the point is to stop one device issuing
    /// hundreds of tickets, not to be exact at the boundary — and a cache outage deliberately
    /// fails open rather than refusing a real customer a ticket.
    /// </para>
    /// </summary>
    private async Task<(bool Allowed, int RetryAfterSeconds)> CheckRateLimitAsync(
        string keyPrefix, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            var windowTicks = window.Ticks;
            var nowTicks = DateTime.UtcNow.Ticks;
            var windowIndex = nowTicks / windowTicks;
            var windowEndTicks = (windowIndex + 1) * windowTicks;
            var retryAfter = Math.Max(1, (int)TimeSpan.FromTicks(windowEndTicks - nowTicks).TotalSeconds);

            var key = $"{keyPrefix}:{windowIndex}";
            var existing = await _cache.GetStringAsync(key, cancellationToken);
            var count = int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

            if (count >= limit)
                return (false, retryAfter);

            await _cache.SetStringAsync(
                key,
                (count + 1).ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = new DateTimeOffset(new DateTime(windowEndTicks, DateTimeKind.Utc))
                },
                cancellationToken);

            return (true, retryAfter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rate-limit check failed for {KeyPrefix}; allowing the request", keyPrefix);
            return (true, 0);
        }
    }

    #endregion
}

public record WaitTimeResponse
{
    public Guid ServiceTypeId { get; init; }
    public string ServiceTypeName { get; init; } = string.Empty;
    public int WaitingCount { get; init; }
    public int EstimatedWaitMinutes { get; init; }
    public int CountersActive { get; init; }
}

public record SendTicketSmsRequest
{
    public string? PhoneNumber { get; init; }
}

/// <summary>
/// Carries both halves of the answer on purpose. <c>Sent</c> is what the person at the kiosk asked
/// about; <c>PhoneStored</c> is true even when the send failed, because the number is kept either
/// way and the automatic "it's your turn" message may still reach them.
/// </summary>
public record SendTicketSmsResponse
{
    public bool Sent { get; init; }
    public bool PhoneStored { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
