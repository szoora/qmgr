namespace QMgr.Domain.Enums;

public enum VisitorStatus
{
    PreRegistered = 0,
    CheckedIn = 1,
    CheckedOut = 2,
    NoShow = 3,
    Cancelled = 4,

    // Pre-registration of an EXPECTED arrival: someone booked in for a specific date/time ahead of
    // the day, usually in a batch (an interview panel, a contractor crew, a governors' meeting).
    // Distinct from PreRegistered — which is the older single "walk-up-desk creates the record a
    // few minutes early" path — only in intent and in carrying Visitor.ExpectedArrivalAt; both
    // convert into a real check-in through exactly the same CheckInExisting action, so nothing
    // downstream has to know which of the two a visit started life as.
    //
    // Stored as a STRING (VisitorConfiguration maps Status with .HasConversion<string>() into a
    // varchar(20)), so adding a value here needs no data migration for existing rows — but note
    // the partial unique index idx_visitors_profile_active_unique filters on "Status" = 'CheckedIn',
    // so this value must never be used for an on-site state.
    Expected = 5
}
