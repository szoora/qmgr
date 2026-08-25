using QMgr.Domain.Enums;

namespace QMgr.API.Services.Printing;

public interface IPrintService
{
    Task<PrintResult> PrintTicketAsync(TicketPrintData ticketData, PrinterSettings settings);
    Task<PrintResult> PrintToNetworkPrinterAsync(byte[] escPosData, string ipAddress, int port);
    Task<List<PrinterInfo>> GetAvailablePrintersAsync();
    byte[] GenerateEscPosTicket(TicketPrintData ticketData, PrinterSettings settings);
    string GenerateHtmlTicket(TicketPrintData ticketData, PrinterSettings settings);
}

public class TicketPrintData
{
    public string TokenNumber { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int QueuePosition { get; set; }
    public int EstimatedWaitMinutes { get; set; }
    public DateTime IssuedAt { get; set; }
    public string? CustomerName { get; set; }
    public string? BranchName { get; set; }
    public string? FeedbackCode { get; set; }
    public string? FeedbackUrl { get; set; }
    public string? QrCodeData { get; set; }
}

public class PrinterSettings
{
    public PrintMethod Method { get; set; } = PrintMethod.BrowserPrint;
    public PrinterType Type { get; set; } = PrinterType.Thermal;
    public string? PrinterName { get; set; }
    public string? IpAddress { get; set; }
    public int Port { get; set; } = 9100;
    public int PaperWidth { get; set; } = 80;
    public bool PrintLogo { get; set; } = true;
    public string? LogoUrl { get; set; }
    public bool PrintQrCode { get; set; } = true;
    public bool PrintFeedbackUrl { get; set; } = true;
    public string? HeaderText { get; set; }
    public string? FooterText { get; set; }
    public int FontSize { get; set; } = 12;
}

public class PrintResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ErrorCode { get; set; }
    public byte[]? RawData { get; set; }
    public string? HtmlContent { get; set; }
}

public class PrinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool IsOnline { get; set; }
    public string? PortName { get; set; }
}
