namespace QMgr.Domain.Enums;

public enum TokenStatus
{
    Waiting = 0,
    Called = 1,
    Serving = 2,
    Completed = 3,
    Cancelled = 4,
    NoShow = 5,
    Transferred = 6
}

public enum TokenPriority
{
    Normal = 0,
    Priority = 1,
    VIP = 2,
    Emergency = 3
}

public enum TokenSource
{
    Kiosk = 0,
    Web = 1,
    Mobile = 2,
    API = 3,
    Appointment = 4,
    WalkIn = 5
}
