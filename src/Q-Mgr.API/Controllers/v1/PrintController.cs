using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Application.DTOs;
using QMgr.API.Authorization;
using QMgr.API.Services.Printing;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public class PrintController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly IPrintService _printService;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ILogger<PrintController> _logger;

    public PrintController(
        QMgrDbContext context,
        IPrintService printService,
        ITenantContextAccessor tenantAccessor,
        ILogger<PrintController> logger)
    {
        _context = context;
        _printService = printService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Verifies that the branch belongs to the current organization. SECURITY: every action in
    /// this controller takes a raw branchId route param with no other tenant scoping (unlike
    /// TokensController's [Route] on branchId, PrintController's is per-action) — without this,
    /// any authenticated user holding the relevant permission in their own org could read or
    /// overwrite another tenant's printer/kiosk settings or print another tenant's ticket by
    /// supplying a foreign branchId.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
            return null;

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    /// <summary>
    /// Get printer settings for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/printer-settings")]
    [RequirePermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(PrinterSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterSettings(Guid branchId)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
        {
            // Return defaults
            return Ok(new PrinterSettingsDto
            {
                BranchId = branchId,
                PreferredPrintMethod = PrintMethod.BrowserPrint,
                PrinterType = PrinterType.Thermal,
                ThermalPaperWidth = 80,
                PrintLogo = true,
                PrintQrCode = true,
                PrintFeedbackUrl = true,
                PrintFontSize = 12,
                AutoPrintOnTokenCreate = false
            });
        }

        return Ok(new PrinterSettingsDto
        {
            BranchId = branchId,
            PreferredPrintMethod = settings.PreferredPrintMethod,
            PrinterType = settings.PrinterType,
            PrinterName = settings.PrinterName,
            PrinterIpAddress = settings.PrinterIpAddress,
            PrinterPort = settings.PrinterPort,
            ThermalPaperWidth = settings.ThermalPaperWidth,
            PrintLogo = settings.PrintLogo,
            PrintLogoUrl = settings.PrintLogoUrl,
            PrintQrCode = settings.PrintQrCode,
            PrintFeedbackUrl = settings.PrintFeedbackUrl,
            PrintHeaderText = settings.PrintHeaderText,
            PrintFooterText = settings.PrintFooterText,
            PrintFontSize = settings.PrintFontSize,
            AutoPrintOnTokenCreate = settings.AutoPrintOnTokenCreate
        });
    }

    /// <summary>
    /// Update printer settings for a branch
    /// </summary>
    [HttpPut("branches/{branchId:guid}/printer-settings")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(PrinterSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePrinterSettings(Guid branchId, [FromBody] UpdatePrinterSettingsRequest request)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
        {
            settings = new QMgr.Domain.Entities.Organization.BranchSettings
            {
                Id = Guid.NewGuid(),
                BranchId = branchId
            };
            _context.BranchSettings.Add(settings);
        }

        settings.PreferredPrintMethod = request.PreferredPrintMethod;
        settings.PrinterType = request.PrinterType;
        settings.PrinterName = request.PrinterName;
        settings.PrinterIpAddress = request.PrinterIpAddress;
        settings.PrinterPort = request.PrinterPort;
        settings.ThermalPaperWidth = request.ThermalPaperWidth;
        settings.PrintLogo = request.PrintLogo;
        settings.PrintLogoUrl = request.PrintLogoUrl;
        settings.PrintQrCode = request.PrintQrCode;
        settings.PrintFeedbackUrl = request.PrintFeedbackUrl;
        settings.PrintHeaderText = request.PrintHeaderText;
        settings.PrintFooterText = request.PrintFooterText;
        settings.PrintFontSize = request.PrintFontSize;
        settings.AutoPrintOnTokenCreate = request.AutoPrintOnTokenCreate;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated printer settings for branch {BranchId}", branchId);

        return Ok(new PrinterSettingsDto
        {
            BranchId = branchId,
            PreferredPrintMethod = settings.PreferredPrintMethod,
            PrinterType = settings.PrinterType,
            PrinterName = settings.PrinterName,
            PrinterIpAddress = settings.PrinterIpAddress,
            PrinterPort = settings.PrinterPort,
            ThermalPaperWidth = settings.ThermalPaperWidth,
            PrintLogo = settings.PrintLogo,
            PrintLogoUrl = settings.PrintLogoUrl,
            PrintQrCode = settings.PrintQrCode,
            PrintFeedbackUrl = settings.PrintFeedbackUrl,
            PrintHeaderText = settings.PrintHeaderText,
            PrintFooterText = settings.PrintFooterText,
            PrintFontSize = settings.PrintFontSize,
            AutoPrintOnTokenCreate = settings.AutoPrintOnTokenCreate
        });
    }

    /// <summary>
    /// Get kiosk settings for a branch
    /// </summary>
    [HttpGet("branches/{branchId:guid}/kiosk-settings")]
    [RequirePermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(KioskSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKioskSettings(Guid branchId)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings?.KioskSettingsJson == null)
        {
            // Return defaults
            return Ok(new KioskSettingsDto());
        }

        try
        {
            var kioskSettings = JsonSerializer.Deserialize<KioskSettingsDto>(settings.KioskSettingsJson);
            return Ok(kioskSettings ?? new KioskSettingsDto());
        }
        catch
        {
            return Ok(new KioskSettingsDto());
        }
    }

    /// <summary>
    /// Update kiosk settings for a branch
    /// </summary>
    [HttpPut("branches/{branchId:guid}/kiosk-settings")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(KioskSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateKioskSettings(Guid branchId, [FromBody] KioskSettingsDto request)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var settings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        if (settings == null)
        {
            settings = new QMgr.Domain.Entities.Organization.BranchSettings
            {
                Id = Guid.NewGuid(),
                BranchId = branchId
            };
            _context.BranchSettings.Add(settings);
        }

        settings.KioskSettingsJson = JsonSerializer.Serialize(request);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated kiosk settings for branch {BranchId}", branchId);

        return Ok(request);
    }

    /// <summary>
    /// Print a ticket for a token
    /// </summary>
    [HttpPost("branches/{branchId:guid}/tokens/{tokenId:guid}/print")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(PrintResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PrintTicket(Guid branchId, Guid tokenId, [FromBody] PrintTicketRequest? request = null)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var token = await _context.Tokens
            .Include(t => t.ServiceType)
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId);

        if (token == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Token Not Found",
                Detail = $"Token with ID '{tokenId}' was not found",
                Status = StatusCodes.Status404NotFound
            });
        }

        var branchSettings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        // Get feedback code if exists
        var feedback = await _context.Feedbacks
            .FirstOrDefaultAsync(f => f.TokenId == tokenId);

        // Calculate queue position (number of waiting tokens ahead)
        var queuePosition = await _context.Tokens
            .CountAsync(t => t.BranchId == branchId
                && t.ServiceTypeId == token.ServiceTypeId
                && t.Status == TokenStatus.Waiting
                && t.CreatedAt < token.CreatedAt) + 1;

        var ticketData = new TicketPrintData
        {
            TokenNumber = token.DisplayNumber,
            ServiceName = token.ServiceType?.Name ?? "General Service",
            QueuePosition = queuePosition,
            EstimatedWaitMinutes = token.EstimatedWaitMinutes ?? 0,
            IssuedAt = token.CreatedAt,
            CustomerName = token.CustomerName,
            BranchName = token.Branch?.Name,
            FeedbackCode = feedback?.FeedbackCode,
            FeedbackUrl = feedback != null ? $"/feedback/{feedback.FeedbackCode}" : null,
            QrCodeData = $"token:{tokenId}"
        };

        var printerSettings = new PrinterSettings
        {
            Method = request?.PrintMethod ?? branchSettings?.PreferredPrintMethod ?? PrintMethod.BrowserPrint,
            Type = branchSettings?.PrinterType ?? PrinterType.Thermal,
            PrinterName = branchSettings?.PrinterName,
            IpAddress = request?.PrinterIpAddress ?? branchSettings?.PrinterIpAddress,
            Port = branchSettings?.PrinterPort ?? 9100,
            PaperWidth = branchSettings?.ThermalPaperWidth ?? 80,
            PrintLogo = branchSettings?.PrintLogo ?? true,
            LogoUrl = branchSettings?.PrintLogoUrl,
            PrintQrCode = branchSettings?.PrintQrCode ?? true,
            PrintFeedbackUrl = branchSettings?.PrintFeedbackUrl ?? true,
            HeaderText = branchSettings?.PrintHeaderText,
            FooterText = branchSettings?.PrintFooterText,
            FontSize = branchSettings?.PrintFontSize ?? 12
        };

        var result = await _printService.PrintTicketAsync(ticketData, printerSettings);

        return Ok(new PrintResultDto
        {
            Success = result.Success,
            Message = result.Message,
            ErrorCode = result.ErrorCode,
            EscPosData = result.RawData != null ? Convert.ToBase64String(result.RawData) : null,
            HtmlContent = result.HtmlContent
        });
    }

    /// <summary>
    /// Generate ticket data without printing (for client-side printing)
    /// </summary>
    [HttpGet("branches/{branchId:guid}/tokens/{tokenId:guid}/print-data")]
    [RequirePermission(Permissions.TokensView)]
    [ProducesResponseType(typeof(PrintDataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrintData(Guid branchId, Guid tokenId)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var token = await _context.Tokens
            .Include(t => t.ServiceType)
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.BranchId == branchId);

        if (token == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Token Not Found",
                Detail = $"Token with ID '{tokenId}' was not found",
                Status = StatusCodes.Status404NotFound
            });
        }

        var branchSettings = await _context.BranchSettings
            .FirstOrDefaultAsync(s => s.BranchId == branchId);

        var feedback = await _context.Feedbacks
            .FirstOrDefaultAsync(f => f.TokenId == tokenId);

        // Calculate queue position (number of waiting tokens ahead)
        var queuePosition = await _context.Tokens
            .CountAsync(t => t.BranchId == branchId
                && t.ServiceTypeId == token.ServiceTypeId
                && t.Status == TokenStatus.Waiting
                && t.CreatedAt < token.CreatedAt) + 1;

        var ticketData = new TicketPrintData
        {
            TokenNumber = token.DisplayNumber,
            ServiceName = token.ServiceType?.Name ?? "General Service",
            QueuePosition = queuePosition,
            EstimatedWaitMinutes = token.EstimatedWaitMinutes ?? 0,
            IssuedAt = token.CreatedAt,
            CustomerName = token.CustomerName,
            BranchName = token.Branch?.Name,
            FeedbackCode = feedback?.FeedbackCode,
            FeedbackUrl = feedback != null ? $"/feedback/{feedback.FeedbackCode}" : null,
            QrCodeData = $"token:{tokenId}"
        };

        var printerSettings = new PrinterSettings
        {
            Method = branchSettings?.PreferredPrintMethod ?? PrintMethod.BrowserPrint,
            Type = branchSettings?.PrinterType ?? PrinterType.Thermal,
            PaperWidth = branchSettings?.ThermalPaperWidth ?? 80,
            PrintLogo = branchSettings?.PrintLogo ?? true,
            LogoUrl = branchSettings?.PrintLogoUrl,
            PrintQrCode = branchSettings?.PrintQrCode ?? true,
            PrintFeedbackUrl = branchSettings?.PrintFeedbackUrl ?? true,
            HeaderText = branchSettings?.PrintHeaderText,
            FooterText = branchSettings?.PrintFooterText,
            FontSize = branchSettings?.PrintFontSize ?? 12
        };

        var escPosData = _printService.GenerateEscPosTicket(ticketData, printerSettings);
        var htmlContent = _printService.GenerateHtmlTicket(ticketData, printerSettings);

        return Ok(new PrintDataDto
        {
            TokenId = tokenId,
            TokenNumber = token.DisplayNumber,
            PreferredMethod = branchSettings?.PreferredPrintMethod ?? PrintMethod.BrowserPrint,
            PrinterName = branchSettings?.PrinterName,
            PrinterIpAddress = branchSettings?.PrinterIpAddress,
            PrinterPort = branchSettings?.PrinterPort ?? 9100,
            EscPosData = Convert.ToBase64String(escPosData),
            HtmlContent = htmlContent,
            AutoPrint = branchSettings?.AutoPrintOnTokenCreate ?? false
        });
    }

    /// <summary>
    /// Test printer connection
    /// </summary>
    [HttpPost("branches/{branchId:guid}/printer-settings/test")]
    [RequirePermission(Permissions.SettingsEdit)]
    [ProducesResponseType(typeof(PrintResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestPrinter(Guid branchId, [FromBody] TestPrinterRequest request)
    {
        var verifyResult = await VerifyBranchOwnership(branchId);
        if (verifyResult != null) return verifyResult;

        var testData = new TicketPrintData
        {
            TokenNumber = "TEST",
            ServiceName = "Test Print",
            QueuePosition = 1,
            EstimatedWaitMinutes = 5,
            IssuedAt = DateTime.Now,
            BranchName = "Test Branch"
        };

        var settings = new PrinterSettings
        {
            Method = request.PrintMethod,
            IpAddress = request.PrinterIpAddress,
            Port = request.PrinterPort,
            PaperWidth = request.ThermalPaperWidth,
            PrintQrCode = false,
            PrintFeedbackUrl = false,
            HeaderText = "=== PRINTER TEST ===",
            FooterText = "Test completed successfully!"
        };

        var result = await _printService.PrintTicketAsync(testData, settings);

        return Ok(new PrintResultDto
        {
            Success = result.Success,
            Message = result.Message,
            ErrorCode = result.ErrorCode
        });
    }

    /// <summary>
    /// Get list of available printers on the server
    /// </summary>
    [HttpGet("printers")]
    [RequirePermission(Permissions.SettingsView)]
    [ProducesResponseType(typeof(List<PrinterInfoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinters()
    {
        var printers = await _printService.GetAvailablePrintersAsync();

        return Ok(printers.Select(p => new PrinterInfoDto
        {
            Name = p.Name,
            Description = p.Description,
            IsDefault = p.IsDefault,
            IsOnline = p.IsOnline
        }));
    }
}

#region DTOs

public record PrinterSettingsDto
{
    public Guid BranchId { get; init; }
    public PrintMethod PreferredPrintMethod { get; init; }
    public PrinterType PrinterType { get; init; }
    public string? PrinterName { get; init; }
    public string? PrinterIpAddress { get; init; }
    public int PrinterPort { get; init; } = 9100;
    public int ThermalPaperWidth { get; init; } = 80;
    public bool PrintLogo { get; init; } = true;
    public string? PrintLogoUrl { get; init; }
    public bool PrintQrCode { get; init; } = true;
    public bool PrintFeedbackUrl { get; init; } = true;
    public string? PrintHeaderText { get; init; }
    public string? PrintFooterText { get; init; }
    public int PrintFontSize { get; init; } = 12;
    public bool AutoPrintOnTokenCreate { get; init; }
}

public record UpdatePrinterSettingsRequest
{
    public PrintMethod PreferredPrintMethod { get; init; }
    public PrinterType PrinterType { get; init; }
    public string? PrinterName { get; init; }
    public string? PrinterIpAddress { get; init; }
    public int PrinterPort { get; init; } = 9100;
    public int ThermalPaperWidth { get; init; } = 80;
    public bool PrintLogo { get; init; } = true;
    public string? PrintLogoUrl { get; init; }
    public bool PrintQrCode { get; init; } = true;
    public bool PrintFeedbackUrl { get; init; } = true;
    public string? PrintHeaderText { get; init; }
    public string? PrintFooterText { get; init; }
    public int PrintFontSize { get; init; } = 12;
    public bool AutoPrintOnTokenCreate { get; init; }
}

public record PrintTicketRequest
{
    public PrintMethod? PrintMethod { get; init; }
    public string? PrinterIpAddress { get; init; }
}

public record PrintResultDto
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public string? EscPosData { get; init; }
    public string? HtmlContent { get; init; }
}

public record PrintDataDto
{
    public Guid TokenId { get; init; }
    public string TokenNumber { get; init; } = string.Empty;
    public PrintMethod PreferredMethod { get; init; }
    public string? PrinterName { get; init; }
    public string? PrinterIpAddress { get; init; }
    public int PrinterPort { get; init; }
    public string? EscPosData { get; init; }
    public string? HtmlContent { get; init; }
    public bool AutoPrint { get; init; }
}

public record TestPrinterRequest
{
    public PrintMethod PrintMethod { get; init; }
    public string? PrinterIpAddress { get; init; }
    public int PrinterPort { get; init; } = 9100;
    public int ThermalPaperWidth { get; init; } = 80;
}

public record PrinterInfoDto
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
    public bool IsOnline { get; init; }
}

#endregion
