namespace QMgr.Domain.Enums;

/// <summary>
/// Lifecycle of a scheduled appointment. Deliberately separate from <see cref="TokenStatus"/>:
/// an appointment exists before anyone is in a queue, and only becomes a live queue ticket at
/// check-in (which sets <see cref="CheckedIn"/> and links the created token). Terminal states are
/// Completed, Cancelled and NoShow — nothing moves out of those.
/// </summary>
public enum AppointmentStatus
{
    /// <summary>Slot reserved; nobody has confirmed it yet. The state every booking starts in.</summary>
    Booked = 0,

    /// <summary>The customer (or staff on their behalf) confirmed they are coming.</summary>
    Confirmed = 1,

    /// <summary>The customer arrived and a real queue token was issued — see Appointment.TokenId.</summary>
    CheckedIn = 2,

    /// <summary>Service was delivered.</summary>
    Completed = 3,

    /// <summary>Cancelled by customer, staff, or an integration.</summary>
    Cancelled = 4,

    /// <summary>The scheduled time passed with no check-in — set by hand or by AppointmentJobs' sweep.</summary>
    NoShow = 5
}
