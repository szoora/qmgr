using Microsoft.AspNetCore.DataProtection;
using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services;

public class VisitorBadgeTokenService : IVisitorBadgeTokenService
{
    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<VisitorBadgeTokenService> _logger;

    public VisitorBadgeTokenService(IDataProtectionProvider dataProtectionProvider, ILogger<VisitorBadgeTokenService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector("Visitor.Badge.Tokens.v1").ToTimeLimitedDataProtector();
        _logger = logger;
    }

    public string IssueVisitToken(Guid visitorId, Guid branchId, DateTime expiresAtUtc) =>
        _protector.Protect($"visit:{visitorId:N}:{branchId:N}", expiresAtUtc);

    public string IssuePassToken(Guid passId, Guid branchId, DateTime expiresAtUtc) =>
        _protector.Protect($"pass:{passId:N}:{branchId:N}", expiresAtUtc);

    public VisitorBadgeTokenPayload? TryDecode(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (Exception ex)
        {
            // Expired, tampered, or signed with a since-rotated key — all indistinguishable to
            // the caller and all treated the same way: reject the scan, don't throw.
            _logger.LogWarning(ex, "Rejected an invalid or expired visitor badge token");
            return null;
        }

        var parts = payload.Split(':');
        if (parts.Length != 3) return null;

        var kind = parts[0] switch
        {
            "visit" => VisitorBadgeTokenKind.Visit,
            "pass" => VisitorBadgeTokenKind.Pass,
            _ => (VisitorBadgeTokenKind?)null
        };
        if (kind == null) return null;

        if (!Guid.TryParseExact(parts[1], "N", out var id) || !Guid.TryParseExact(parts[2], "N", out var branchId))
            return null;

        return new VisitorBadgeTokenPayload(kind.Value, id, branchId);
    }
}
