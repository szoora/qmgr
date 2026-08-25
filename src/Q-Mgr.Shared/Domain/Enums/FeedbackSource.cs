namespace QMgr.Domain.Enums;

public enum FeedbackSource
{
    Kiosk = 0,      // Submitted at kiosk after service
    Link = 1,       // Submitted via unique feedback link (offsite)
    Mobile = 2,     // Submitted via mobile app
    Web = 3         // Submitted via web portal
}

public enum FeedbackCategory
{
    General = 0,
    ServiceQuality = 1,
    WaitTime = 2,
    StaffBehavior = 3,
    Facility = 4,
    Other = 5
}
