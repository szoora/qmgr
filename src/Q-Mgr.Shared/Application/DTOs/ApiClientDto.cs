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
}

public record CreateApiClientRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? SystemType { get; init; }
    public bool IsActive { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
}

public record UpdateApiClientRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? SystemType { get; init; }
    public bool IsActive { get; init; } = true;
    public List<string> Scopes { get; init; } = new();
}

/// <summary>
/// Returned only once, immediately after Create or Regenerate — the plaintext client secret
/// is never persisted or retrievable again after this response (only its BCrypt hash is
/// stored), matching standard API-key UX (Stripe/GitHub/AWS all work the same way).
/// </summary>
public record ApiClientSecretRevealDto
{
    public ApiClientDto Client { get; init; } = new();
    public string ClientSecret { get; init; } = string.Empty;
}
