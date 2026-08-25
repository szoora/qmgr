namespace QMgr.Domain.Enums;

public enum ContentType
{
    Image = 0,
    Video = 1,
    Audio = 2,
    PowerPoint = 3,
    Html = 4,
    Text = 5,
    Pdf = 6
}

public enum StorageType
{
    Local = 0,
    AzureBlob = 1,
    S3 = 2,
    Url = 3
}

public enum DisplayType
{
    CustomerDisplay = 0,
    Kiosk = 1,
    Counter = 2,
    Signage = 3
}

public enum ZoneType
{
    MainQueue = 0,
    Advertisement = 1,
    Ticker = 2,
    Clock = 3,
    Weather = 4,
    Custom = 5
}
