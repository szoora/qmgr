namespace QMgr.Domain.Enums;

/// <summary>
/// Recorded for safeguarding practice, dormitory placement and the disproportionality reporting
/// that checks whether punitive responses land unevenly. Nullable on the student — a school that
/// does not collect it simply leaves it unset rather than being forced to guess.
/// </summary>
public enum StudentSex
{
    Female = 0,
    Male = 1,
    Other = 2
}

/// <summary>
/// The single highest-value welfare field on a student. It decides who is responsible at night,
/// whether "sent home" is even an available response, and how quickly a guardian can be reached.
/// </summary>
public enum StudentResidency
{
    Day = 0,
    Boarder = 1,
    Weekly = 2
}

/// <summary>
/// Household composition, which predicts welfare need more reliably than an orphan label alone —
/// a child living with a grandmother and a child living with both parents are different pictures
/// even when neither is an orphan.
/// </summary>
public enum StudentLivesWith
{
    BothParents = 0,
    OneParent = 1,
    Grandparent = 2,
    OtherRelative = 3,
    Guardian = 4,
    Independent = 5
}

/// <summary>
/// A welfare field, not a finance one. Arrears predict absence, withdrawal and shame-driven
/// behaviour long before they show up in a ledger, and a sponsored child has a sponsor who must
/// be told things a parent would be told.
/// </summary>
public enum StudentFeesStatus
{
    Clear = 0,
    Partial = 1,
    InArrears = 2,
    Sponsored = 3,
    Bursary = 4
}

/// <summary>
/// A long unaccompanied walk is a real exposure and the most common honest explanation for
/// lateness — worth knowing before lateness is treated as defiance.
/// </summary>
public enum StudentTransportMode
{
    Walks = 0,
    Boda = 1,
    SchoolBus = 2,
    PublicTransport = 3,
    PrivateVehicle = 4
}

/// <summary>
/// Per-child contact restriction on a guardian link. The organization-wide
/// <c>VisitorProfile.IsWatchlisted</c> cannot express this: it bars a person from the whole site,
/// whereas a custody order routinely bars contact with one child while leaving a sibling
/// unaffected. Stored on <c>StudentGuardian</c> precisely because it is a property of the
/// relationship, not of the person.
/// </summary>
public enum GuardianContactRestriction
{
    None = 0,
    SupervisedOnly = 1,
    MustNotCollect = 2,
    NoContact = 3
}
