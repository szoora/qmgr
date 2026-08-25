namespace QMgr.Domain.Enums;

public enum PrintMethod
{
    /// <summary>
    /// Browser's native print dialog (window.print())
    /// </summary>
    BrowserPrint = 0,

    /// <summary>
    /// Direct thermal printing via QZ Tray
    /// </summary>
    QZTray = 1,

    /// <summary>
    /// WebUSB direct connection to USB printer (Chrome only)
    /// </summary>
    WebUSB = 2,

    /// <summary>
    /// Server-side printing via API (ESC/POS)
    /// </summary>
    ServerPrint = 3,

    /// <summary>
    /// Network printer via raw TCP/IP
    /// </summary>
    NetworkPrinter = 4
}

public enum PrinterType
{
    /// <summary>
    /// Standard thermal receipt printer (58mm or 80mm)
    /// </summary>
    Thermal = 0,

    /// <summary>
    /// Standard A4/Letter printer
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Label printer
    /// </summary>
    Label = 2
}
