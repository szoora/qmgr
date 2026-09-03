namespace QMgr.Application.DTOs;

public record ApiClientDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string? SystemType { get; init; }
    public bool IsActive { get; init; }
    public List<string> Scopes { get; init; } = new();
    public int RateLimitPerMinute { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime CreatedAt { get; init; }

    // Webhooks — outbound subscription (URL + events) and whether a signing secret exists.
    // The secret itself is never returned on reads; see WebhookSecret below.
    public string? WebhookUrl { get; init; }
    public List<string> WebhookEvents { get; init; } = new();
    public bool HasWebhookSecret { get; init; }

    /// <summary>
    /// Populated ONLY on the response of Create / Update (when a new secret was just generated)
    /// and of POST {id}/webhook-secret/rotate. Always null on GET. Unlike the client secret
    /// (BCrypt-hashed), the webhook secret is stored in plaintext because the outbound signer
    /// needs the raw key — but the API still only reveals it on the call that produced it.
    /// </summary>
    public string? WebhookSecret { get; init; }
}

public record CreateApiClientRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? SystemType { get; init; }
    public bool IsActive { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
    public string? WebhookUrl { get; init; }
    public List<string> WebhookEvents { get; init; } = new();
}

public record UpdateApiClientRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? SystemType { get; init; }
    public bool IsActive { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
    public string? WebhookUrl { get; init; }
    public List<string> WebhookEvents { get; init; } = new();
}

/// <summary>
/// Returned only once, immediately after Create or Regenerate — the plaintext client secret
/// is never persisted or retrievable again after this response (only its BCrypt hash is
/// stored), matching standard API-key UX (Stripe/GitHub/AWS all work the same way).
/// On Create, <see cref="ApiClientDto.WebhookSecret"/> inside <see cref="Client"/> is also
/// populated when a webhook URL was supplied (a signing secret is generated alongside).
/// </summary>
public record ApiClientSecretRevealDto
{
    public ApiClientDto Client { get; init; } = new();
    public string ClientSecret { get; init; } = string.Empty;
}

/// <summary>One entry of the canonical webhook event catalog (GET api/v1/api-clients/webhook-events).</summary>
public record WebhookEventDto
{
    public string Name { get; init; } = string.Empty;
    /// <summary>"outbound" = Q-Mgr pushes it to the client's WebhookUrl; "inbound" = the client posts it to Q-Mgr.</summary>
    public string Direction { get; init; } = "outbound";
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Single source of truth for webhook event names, shared by the API (validation, inbound
/// dispatch, the catalog endpoint) and the Web UI (event picker). Outbound names must match
/// the strings WebhookService fans out; inbound names must match WebhooksController's switch.
/// </summary>
public static class WebhookEventCatalog
{
    public const string TokenCreated = "token.created";
    public const string TokenCalled = "token.called";
    public const string TokenServing = "token.serving";
    public const string TokenCompleted = "token.completed";
    public const string TokenCancelled = "token.cancelled";
    public const string TokenNoShow = "token.no_show";

    public const string AppointmentCreated = "appointment.created";
    public const string AppointmentCancelled = "appointment.cancelled";

    public static readonly IReadOnlyList<WebhookEventDto> Outbound = new List<WebhookEventDto>
    {
        new() { Name = TokenCreated,   Direction = "outbound", Description = "A token joined the queue (kiosk, web, API or inbound webhook)" },
        new() { Name = TokenCalled,    Direction = "outbound", Description = "A token was called to a counter" },
        new() { Name = TokenServing,   Direction = "outbound", Description = "Service started for a token at a counter" },
        new() { Name = TokenCompleted, Direction = "outbound", Description = "Service finished for a token" },
        new() { Name = TokenCancelled, Direction = "outbound", Description = "A token was cancelled (by staff, customer or integration)" },
        new() { Name = TokenNoShow,    Direction = "outbound", Description = "A called token did not show up" }
    };

    public static readonly IReadOnlyList<WebhookEventDto> Inbound = new List<WebhookEventDto>
    {
        new() { Name = AppointmentCreated,   Direction = "inbound", Description = "Create a queue token for an appointment booked in your system" },
        new() { Name = AppointmentCancelled, Direction = "inbound", Description = "Cancel the token previously created for an external reference" }
    };

    public static IReadOnlyList<WebhookEventDto> All => Outbound.Concat(Inbound).ToList();

    public static bool IsOutbound(string name) =>
        Outbound.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
