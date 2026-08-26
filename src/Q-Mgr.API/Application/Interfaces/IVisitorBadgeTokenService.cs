namespace QMgr.Application.Interfaces;

public enum VisitorBadgeTokenKind { Visit, Pass }

public record VisitorBadgeTokenPayload(VisitorBadgeTokenKind Kind, Guid Id, Guid BranchId);

/// <summary>
/// Issues and validates the opaque, signed, time-limited tokens embedded in a visitor's QR
/// badge. Built on ASP.NET's own Data Protection stack (already configured in Program.cs with a
/// persisted key ring) — no third-party crypto/QR dependency added to the server. A badge is
/// never trusted on the strength of the token alone: every scan re-checks the referenced
/// Visit/Pass's live DB state (expiry, revocation, current status), so a photographed QR stops
/// working the instant the underlying record does, not just when the token's own clock runs out.
/// </summary>
public interface IVisitorBadgeTokenService
{
    string IssueVisitToken(Guid visitorId, Guid branchId, DateTime expiresAtUtc);
    string IssuePassToken(Guid passId, Guid branchId, DateTime expiresAtUtc);

    /// <summary>Returns null for a missing, tampered, wrongly-scoped, or expired token — never throws.</summary>
    VisitorBadgeTokenPayload? TryDecode(string token);
}
