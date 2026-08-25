using System.Net.Sockets;
using System.Text;
using QMgr.Domain.Enums;

namespace QMgr.API.Services.Printing;

public class PrintService : IPrintService
{
    private readonly ILogger<PrintService> _logger;

    public PrintService(ILogger<PrintService> logger)
    {
        _logger = logger;
    }

    public async Task<PrintResult> PrintTicketAsync(TicketPrintData ticketData, PrinterSettings settings)
    {
        try
        {
            switch (settings.Method)
            {
                case PrintMethod.ServerPrint:
                case PrintMethod.NetworkPrinter:
                    if (string.IsNullOrEmpty(settings.IpAddress))
                    {
                        return new PrintResult
                        {
                            Success = false,
                            Message = "Printer IP address not configured",
                            ErrorCode = "NO_IP"
                        };
                    }
                    var escPosData = GenerateEscPosTicket(ticketData, settings);
                    return await PrintToNetworkPrinterAsync(escPosData, settings.IpAddress, settings.Port);

                case PrintMethod.BrowserPrint:
                case PrintMethod.QZTray:
                case PrintMethod.WebUSB:
                    // These are handled client-side, return the data needed
                    return new PrintResult
                    {
                        Success = true,
                        RawData = GenerateEscPosTicket(ticketData, settings),
                        HtmlContent = GenerateHtmlTicket(ticketData, settings),
                        Message = "Print data generated successfully"
                    };

                default:
                    return new PrintResult
                    {
                        Success = false,
                        Message = "Unknown print method",
                        ErrorCode = "UNKNOWN_METHOD"
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing ticket {TokenNumber}", ticketData.TokenNumber);
            return new PrintResult
            {
                Success = false,
                Message = ex.Message,
                ErrorCode = "PRINT_ERROR"
            };
        }
    }

    public async Task<PrintResult> PrintToNetworkPrinterAsync(byte[] escPosData, string ipAddress, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ipAddress, port);

            if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
            {
                return new PrintResult
                {
                    Success = false,
                    Message = "Connection timeout - printer not responding",
                    ErrorCode = "TIMEOUT"
                };
            }

            await connectTask; // Ensure connection completed

            using var stream = client.GetStream();
            await stream.WriteAsync(escPosData);
            await stream.FlushAsync();

            _logger.LogInformation("Successfully sent {Bytes} bytes to printer at {IP}:{Port}",
                escPosData.Length, ipAddress, port);

            return new PrintResult
            {
                Success = true,
                Message = "Ticket printed successfully"
            };
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Socket error connecting to printer at {IP}:{Port}", ipAddress, port);
            return new PrintResult
            {
                Success = false,
                Message = $"Cannot connect to printer: {ex.Message}",
                ErrorCode = "SOCKET_ERROR"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error printing to network printer at {IP}:{Port}", ipAddress, port);
            return new PrintResult
            {
                Success = false,
                Message = ex.Message,
                ErrorCode = "PRINT_ERROR"
            };
        }
    }

    public Task<List<PrinterInfo>> GetAvailablePrintersAsync()
    {
        // On Windows, we could use System.Drawing.Printing.PrinterSettings
        // For cross-platform, return empty list - printers are configured manually
        var printers = new List<PrinterInfo>();

        try
        {
            #if WINDOWS
            foreach (string printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                var ps = new System.Drawing.Printing.PrinterSettings { PrinterName = printer };
                printers.Add(new PrinterInfo
                {
                    Name = printer,
                    IsDefault = ps.IsDefaultPrinter,
                    IsOnline = ps.IsValid
                });
            }
            #endif
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate system printers");
        }

        return Task.FromResult(printers);
    }

    public byte[] GenerateEscPosTicket(TicketPrintData ticketData, PrinterSettings settings)
    {
        var commands = new List<byte>();

        // ESC/POS Commands
        var ESC = (byte)0x1B;
        var GS = (byte)0x1D;
        var LF = (byte)0x0A;

        // Initialize printer
        commands.AddRange(new byte[] { ESC, (byte)'@' });

        // Set character code table (PC437)
        commands.AddRange(new byte[] { ESC, (byte)'t', 0 });

        // Center alignment
        commands.AddRange(new byte[] { ESC, (byte)'a', 1 });

        // Bold on, double height/width for header
        commands.AddRange(new byte[] { ESC, (byte)'E', 1 });
        commands.AddRange(new byte[] { GS, (byte)'!', 0x11 }); // Double width and height

        // Header
        if (!string.IsNullOrEmpty(settings.HeaderText))
        {
            commands.AddRange(Encoding.ASCII.GetBytes(settings.HeaderText));
            commands.Add(LF);
        }
        else if (!string.IsNullOrEmpty(ticketData.BranchName))
        {
            commands.AddRange(Encoding.ASCII.GetBytes(ticketData.BranchName));
            commands.Add(LF);
        }

        // Reset to normal size
        commands.AddRange(new byte[] { GS, (byte)'!', 0x00 });
        commands.AddRange(new byte[] { ESC, (byte)'E', 0 });

        // Line
        commands.AddRange(Encoding.ASCII.GetBytes(new string('-', settings.PaperWidth == 58 ? 32 : 48)));
        commands.Add(LF);

        // Token Number - Extra large
        commands.AddRange(new byte[] { GS, (byte)'!', 0x77 }); // Quad width and height
        commands.AddRange(new byte[] { ESC, (byte)'E', 1 }); // Bold
        commands.AddRange(Encoding.ASCII.GetBytes(ticketData.TokenNumber));
        commands.Add(LF);
        commands.Add(LF);

        // Reset size
        commands.AddRange(new byte[] { GS, (byte)'!', 0x00 });
        commands.AddRange(new byte[] { ESC, (byte)'E', 0 });

        // Service name - double height
        commands.AddRange(new byte[] { GS, (byte)'!', 0x10 });
        commands.AddRange(Encoding.ASCII.GetBytes(ticketData.ServiceName));
        commands.Add(LF);
        commands.AddRange(new byte[] { GS, (byte)'!', 0x00 });

        // Line
        commands.AddRange(Encoding.ASCII.GetBytes(new string('-', settings.PaperWidth == 58 ? 32 : 48)));
        commands.Add(LF);

        // Left alignment for details
        commands.AddRange(new byte[] { ESC, (byte)'a', 0 });

        // Queue position
        commands.AddRange(Encoding.ASCII.GetBytes($"Queue Position: #{ticketData.QueuePosition}"));
        commands.Add(LF);

        // Estimated wait
        commands.AddRange(Encoding.ASCII.GetBytes($"Est. Wait: ~{ticketData.EstimatedWaitMinutes} min"));
        commands.Add(LF);

        // Time issued
        commands.AddRange(Encoding.ASCII.GetBytes($"Issued: {ticketData.IssuedAt:HH:mm:ss}"));
        commands.Add(LF);

        // Customer name if provided
        if (!string.IsNullOrEmpty(ticketData.CustomerName))
        {
            commands.AddRange(Encoding.ASCII.GetBytes($"Customer: {ticketData.CustomerName}"));
            commands.Add(LF);
        }

        // Line
        commands.AddRange(Encoding.ASCII.GetBytes(new string('-', settings.PaperWidth == 58 ? 32 : 48)));
        commands.Add(LF);

        // Center for QR code section
        commands.AddRange(new byte[] { ESC, (byte)'a', 1 });

        // QR Code for token tracking (if enabled)
        if (settings.PrintQrCode && !string.IsNullOrEmpty(ticketData.QrCodeData))
        {
            commands.AddRange(GenerateQrCodeCommands(ticketData.QrCodeData));
            commands.Add(LF);
        }

        // Feedback URL
        if (settings.PrintFeedbackUrl && !string.IsNullOrEmpty(ticketData.FeedbackCode))
        {
            commands.Add(LF);
            commands.AddRange(Encoding.ASCII.GetBytes("Give us feedback:"));
            commands.Add(LF);

            // Print feedback URL or code
            if (!string.IsNullOrEmpty(ticketData.FeedbackUrl))
            {
                commands.AddRange(new byte[] { GS, (byte)'!', 0x01 }); // Double width
                commands.AddRange(Encoding.ASCII.GetBytes(ticketData.FeedbackUrl));
                commands.AddRange(new byte[] { GS, (byte)'!', 0x00 });
            }
            else
            {
                commands.AddRange(Encoding.ASCII.GetBytes($"Code: {ticketData.FeedbackCode}"));
            }
            commands.Add(LF);
        }

        // Footer
        commands.Add(LF);
        if (!string.IsNullOrEmpty(settings.FooterText))
        {
            commands.AddRange(Encoding.ASCII.GetBytes(settings.FooterText));
            commands.Add(LF);
        }
        else
        {
            commands.AddRange(Encoding.ASCII.GetBytes("Thank you for waiting!"));
            commands.Add(LF);
            commands.AddRange(Encoding.ASCII.GetBytes("Please listen for your number."));
            commands.Add(LF);
        }

        // Feed and cut
        commands.Add(LF);
        commands.Add(LF);
        commands.Add(LF);
        commands.AddRange(new byte[] { GS, (byte)'V', 66, 3 }); // Partial cut with feed

        return commands.ToArray();
    }

    private byte[] GenerateQrCodeCommands(string data)
    {
        var commands = new List<byte>();
        var GS = (byte)0x1D;
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var storeLen = dataBytes.Length + 3;

        // QR Code: Set model
        commands.AddRange(new byte[] { GS, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0 });

        // QR Code: Set size (module size 4)
        commands.AddRange(new byte[] { GS, (byte)'(', (byte)'k', 3, 0, 49, 67, 6 });

        // QR Code: Set error correction level (L)
        commands.AddRange(new byte[] { GS, (byte)'(', (byte)'k', 3, 0, 49, 69, 48 });

        // QR Code: Store data
        commands.AddRange(new byte[] { GS, (byte)'(', (byte)'k', (byte)(storeLen % 256), (byte)(storeLen / 256), 49, 80, 48 });
        commands.AddRange(dataBytes);

        // QR Code: Print
        commands.AddRange(new byte[] { GS, (byte)'(', (byte)'k', 3, 0, 49, 81, 48 });

        return commands.ToArray();
    }

    public string GenerateHtmlTicket(TicketPrintData ticketData, PrinterSettings settings)
    {
        var width = settings.PaperWidth == 58 ? "58mm" : "80mm";

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>Queue Ticket - {ticketData.TokenNumber}</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{
            font-family: 'Courier New', monospace;
            width: {width};
            padding: 10px;
            background: white;
            color: black;
        }}
        .ticket {{
            text-align: center;
        }}
        .header {{
            font-size: 14px;
            font-weight: bold;
            margin-bottom: 10px;
            padding-bottom: 10px;
            border-bottom: 1px dashed #000;
        }}
        .token-number {{
            font-size: 48px;
            font-weight: bold;
            margin: 20px 0;
            letter-spacing: 4px;
        }}
        .service-name {{
            font-size: 16px;
            font-weight: bold;
            margin-bottom: 15px;
        }}
        .divider {{
            border-top: 1px dashed #000;
            margin: 10px 0;
        }}
        .details {{
            text-align: left;
            font-size: 12px;
            line-height: 1.6;
        }}
        .details .row {{
            display: flex;
            justify-content: space-between;
        }}
        .qr-section {{
            margin: 15px 0;
            text-align: center;
        }}
        .qr-code {{
            width: 100px;
            height: 100px;
            margin: 10px auto;
            border: 1px solid #000;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 10px;
        }}
        .feedback-section {{
            margin: 15px 0;
            padding: 10px;
            background: #f5f5f5;
            border-radius: 4px;
        }}
        .feedback-label {{
            font-size: 10px;
            margin-bottom: 5px;
        }}
        .feedback-code {{
            font-size: 14px;
            font-weight: bold;
            letter-spacing: 2px;
        }}
        .footer {{
            margin-top: 15px;
            padding-top: 10px;
            border-top: 1px dashed #000;
            font-size: 11px;
        }}
        .datetime {{
            font-size: 10px;
            color: #666;
            margin-top: 10px;
        }}
        @@media print {{
            body {{ width: {width}; }}
            .no-print {{ display: none; }}
        }}
    </style>
</head>
<body>
    <div class=""ticket"">
        <div class=""header"">
            {(string.IsNullOrEmpty(settings.HeaderText) ? ticketData.BranchName ?? "Queue Management" : settings.HeaderText)}
        </div>

        <div class=""token-number"">{ticketData.TokenNumber}</div>

        <div class=""service-name"">{ticketData.ServiceName}</div>

        <div class=""divider""></div>

        <div class=""details"">
            <div class=""row"">
                <span>Queue Position:</span>
                <span><strong>#{ticketData.QueuePosition}</strong></span>
            </div>
            <div class=""row"">
                <span>Est. Wait:</span>
                <span><strong>~{ticketData.EstimatedWaitMinutes} min</strong></span>
            </div>
            <div class=""row"">
                <span>Issued:</span>
                <span>{ticketData.IssuedAt:HH:mm:ss}</span>
            </div>
            {(string.IsNullOrEmpty(ticketData.CustomerName) ? "" : $@"
            <div class=""row"">
                <span>Customer:</span>
                <span>{ticketData.CustomerName}</span>
            </div>")}
        </div>

        {(settings.PrintQrCode && !string.IsNullOrEmpty(ticketData.QrCodeData) ? $@"
        <div class=""qr-section"">
            <div class=""qr-code"" id=""qrcode"">QR</div>
            <div style=""font-size: 10px;"">Scan for digital ticket</div>
        </div>" : "")}

        {(settings.PrintFeedbackUrl && !string.IsNullOrEmpty(ticketData.FeedbackCode) ? $@"
        <div class=""feedback-section"">
            <div class=""feedback-label"">Give us feedback:</div>
            <div class=""feedback-code"">{ticketData.FeedbackCode}</div>
            {(string.IsNullOrEmpty(ticketData.FeedbackUrl) ? "" : $@"<div style=""font-size: 9px; margin-top: 5px;"">{ticketData.FeedbackUrl}</div>")}
        </div>" : "")}

        <div class=""footer"">
            {(string.IsNullOrEmpty(settings.FooterText) ? "Thank you for waiting!<br>Please listen for your number." : settings.FooterText)}
        </div>

        <div class=""datetime"">
            {ticketData.IssuedAt:dddd, MMMM dd, yyyy}
        </div>
    </div>

    <script>
        // Auto-print when loaded (for browser print)
        window.onload = function() {{
            if (window.opener) {{
                window.print();
                setTimeout(function() {{ window.close(); }}, 1000);
            }}
        }};
    </script>
</body>
</html>";
    }
}
