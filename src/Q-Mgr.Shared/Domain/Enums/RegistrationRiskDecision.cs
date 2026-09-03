namespace QMgr.Domain.Enums;

/// <summary>
/// What the duplicate check concluded about a sign-up.
/// <para>
/// Three outcomes rather than two, deliberately. The cost of the two error types is not symmetric:
/// a farmed trial account wastes a little storage and can be cleaned up later, whereas a genuine
/// customer who is wrongly refused usually just leaves and never tells anyone why. So only
/// near-certain signals block, and everything more circumstantial lets the customer through while
/// putting the account in front of a human.
/// </para>
/// </summary>
public enum RegistrationRiskDecision
{
    /// <summary>Nothing of concern. The sign-up proceeds normally.</summary>
    Allow = 0,

    /// <summary>
    /// Enough circumstantial overlap to be worth a look. The account is still created and the
    /// customer sees no difference; it simply appears in the platform review queue.
    /// </summary>
    Flag = 1,

    /// <summary>
    /// A near-certain repeat: the same canonical email, or a phone number already verified against
    /// another live account. Refused, with a message pointing the person at signing in or resetting
    /// their password rather than implying they did something wrong.
    /// </summary>
    Block = 2
}

/// <summary>What a platform administrator concluded after looking at a flagged sign-up.</summary>
public enum RegistrationReviewOutcome
{
    /// <summary>A real, distinct customer. The flag was a false positive.</summary>
    Legitimate = 0,

    /// <summary>The same customer as an existing account, but harmless: a second branch, a colleague.</summary>
    DuplicateAllowed = 1,

    /// <summary>A repeat sign-up that should not have been created. The organization is suspended.</summary>
    DuplicateRejected = 2
}
