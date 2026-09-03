using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QMgr.Application.Interfaces.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using QMgr.API.Authorization;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Infrastructure.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;
    private readonly QMgrDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly IStripeService _stripeService;
    private readonly IDistributedCache? _distributedCache;
    private readonly IRequestMetricsService _requestMetrics;
    private readonly IHostEnvironment _hostEnvironment;
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public HealthController(
        ILogger<HealthController> logger,
        QMgrDbContext dbContext,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        IRequestMetricsService requestMetrics,
        IHostEnvironment hostEnvironment,
        IStripeService stripeService)
    {
        _logger = logger;
        _stripeService = stripeService;
        _dbContext = dbContext;
        _configuration = configuration;
        // Redis is only registered when a connection string is configured
        // (see Infrastructure/DependencyInjection.cs) — resolve optionally
        // rather than taking a hard constructor dependency that would fail
        // to resolve when Redis isn't set up.
        _distributedCache = serviceProvider.GetService(typeof(IDistributedCache)) as IDistributedCache;
        _requestMetrics = requestMetrics;
        _hostEnvironment = hostEnvironment;
    }

    /// <summary>
    /// Real connectivity check for Redis (if configured): writes then reads
    /// back a small short-lived key. Returns "Not Configured" rather than a
    /// false "Healthy"/"Critical" when Redis isn't set up at all.
    /// </summary>
    private async Task<(string Status, long ResponseTimeMs)> CheckCacheHealthAsync()
    {
        if (_distributedCache == null)
            return ("Not Configured", 0);

        try
        {
            var sw = Stopwatch.StartNew();
            var key = "health-check-probe";
            var value = Encoding.UTF8.GetBytes(DateTime.UtcNow.Ticks.ToString());
            await _distributedCache.SetAsync(key, value, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10)
            });
            var readBack = await _distributedCache.GetAsync(key);
            sw.Stop();

            return readBack != null ? ("Healthy", sw.ElapsedMilliseconds) : ("Critical", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis health check failed");
            return ("Critical", 0);
        }
    }

    /// <summary>
    /// Real check for Hangfire: whether any background-job server process is
    /// actually registered and reporting in, not just whether the client
    /// services resolved from DI.
    /// </summary>
    private (string Status, int ServerCount) CheckHangfireHealth()
    {
        try
        {
            var servers = JobStorage.Current.GetMonitoringApi().Servers();
            return (servers.Count > 0 ? "Healthy" : "Critical", servers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hangfire health check failed");
            return ("Critical", 0);
        }
    }

    /// <summary>
    /// Basic health check endpoint for connection monitoring
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get()
    {
        return Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        });
    }

    /// <summary>
    /// Comprehensive system health check
    /// </summary>
    [HttpGet("system")]
    [Authorize]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(SystemHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSystemHealth()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var overallStatus = "Healthy";

        // Check database connection
        var dbStatus = "Healthy";
        try
        {
            await _dbContext.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            dbStatus = "Critical";
            overallStatus = "Critical";
            _logger.LogError(ex, "Database health check failed");
        }

        // Check cache (Redis, if configured)
        var (cacheStatus, _) = await CheckCacheHealthAsync();
        if (cacheStatus == "Critical") overallStatus = "Critical";

        // Check background job queue (Hangfire)
        var (queueStatus, _) = CheckHangfireHealth();
        if (queueStatus == "Critical") overallStatus = "Critical";

        // SECURITY: Don't expose environment name in production to prevent reconnaissance
        var isDevelopment = _configuration["ASPNETCORE_ENVIRONMENT"]== "Development";

        return Ok(new SystemHealthDto
        {
            OverallStatus = overallStatus,
            Timestamp = DateTime.UtcNow,
            Uptime = uptime.ToString(@"dd\.hh\:mm\:ss"),
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            DatabaseStatus = dbStatus,
            CacheStatus = cacheStatus,
            QueueStatus = queueStatus,
            Environment = isDevelopment ? "Development" : "Production" // Generic, don't expose staging/other env names
        });
    }

    /// <summary>
    /// Get database health metrics
    /// </summary>
    [HttpGet("database")]
    [Authorize]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(DatabaseHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDatabaseHealth()
    {
        var status = "Healthy";
        var canConnect = false;
        long queryTime = 0;

        try
        {
            var sw = Stopwatch.StartNew();
            canConnect = await _dbContext.Database.CanConnectAsync();
            sw.Stop();
            queryTime = sw.ElapsedMilliseconds;

            if (!canConnect)
                status = "Critical";
            else if (queryTime > 1000)
                status = "Warning";
        }
        catch (Exception ex)
        {
            status = "Critical";
            _logger.LogError(ex, "Database health check failed");
        }

        // Get table counts
        var organizationsCount = await _dbContext.Organizations.CountAsync();
        var usersCount = await _dbContext.Users.CountAsync();
        var tokensCount = await _dbContext.Tokens.CountAsync();

        return Ok(new DatabaseHealthDto
        {
            Status = status,
            CanConnect = canConnect,
            ResponseTimeMs = queryTime,
            TotalOrganizations = organizationsCount,
            TotalUsers = usersCount,
            TotalTokens = tokensCount,
            // SECURITY: Don't expose database provider name (reconnaissance value for attackers)
            DatabaseProvider = "SQL Database", // Generic, don't expose PostgreSQL/MSSQL/etc.
            LastBackup = ReadLastBackupTimestamp()
        });
    }

    /// <summary>
    /// Get service dependencies health status
    /// </summary>
    [HttpGet("services")]
    [Authorize]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(List<ServiceHealthDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetServicesHealth()
    {
        var services = new List<ServiceHealthDto>();

        // Database service
        var dbHealthy = false;
        long dbResponseTime = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            dbHealthy = await _dbContext.Database.CanConnectAsync();
            sw.Stop();
            dbResponseTime = sw.ElapsedMilliseconds;
        }
        catch { }

        services.Add(new ServiceHealthDto
        {
            ServiceName = "PostgreSQL Database",
            Status = dbHealthy ? "Healthy" : "Critical",
            ResponseTimeMs = dbResponseTime,
            LastChecked = DateTime.UtcNow
        });

        // Redis cache
        var (cacheStatus, cacheResponseTime) = await CheckCacheHealthAsync();
        services.Add(new ServiceHealthDto
        {
            ServiceName = "Redis Cache",
            Status = cacheStatus,
            ResponseTimeMs = cacheResponseTime,
            LastChecked = DateTime.UtcNow
        });

        // Hangfire (background jobs) — real check: is any server actually
        // registered and reporting in, not just "did the client resolve".
        var (hangfireStatus, serverCount) = CheckHangfireHealth();
        services.Add(new ServiceHealthDto
        {
            ServiceName = $"Hangfire Background Jobs ({serverCount} server{(serverCount == 1 ? "" : "s")})",
            Status = hangfireStatus,
            ResponseTimeMs = 0,
            LastChecked = DateTime.UtcNow
        });

        // SignalR has no built-in centralized health signal to probe (it's
        // per-connection, not a pingable dependency like a DB or cache) —
        // reporting "Unknown" honestly rather than a hardcoded false
        // "Healthy", same pattern already used below for Stripe.
        services.Add(new ServiceHealthDto
        {
            ServiceName = "SignalR Hubs",
            Status = "Unknown",
            ResponseTimeMs = 0,
            LastChecked = DateTime.UtcNow
        });

        // Stripe API (if configured — Platform Settings row first, then configuration)
        if (await _stripeService.IsConfiguredAsync())
        {
            services.Add(new ServiceHealthDto
            {
                ServiceName = "Stripe Payment Gateway",
                Status = "Unknown", // TODO: Ping Stripe API
                ResponseTimeMs = 0,
                LastChecked = DateTime.UtcNow
            });
        }

        return Ok(services);
    }

    /// <summary>
    /// Get system performance metrics
    /// </summary>
    [HttpGet("performance")]
    [Authorize]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(PerformanceMetricsDto), StatusCodes.Status200OK)]
    public IActionResult GetPerformanceMetrics()
    {
        var process = Process.GetCurrentProcess();

        // Memory metrics
        var memoryUsedMb = process.WorkingSet64 / 1024 / 1024;
        var gcMemoryMb = GC.GetTotalMemory(false) / 1024 / 1024;

        // CPU metrics (approximation)
        var cpuTime = process.TotalProcessorTime;

        var errorRate = _requestMetrics.GetErrorRatePercent();

        return Ok(new PerformanceMetricsDto
        {
            MemoryUsedMb = memoryUsedMb,
            GcMemoryMb = gcMemoryMb,
            ThreadCount = process.Threads.Count,
            CpuTimeSeconds = (long)cpuTime.TotalSeconds,
            Uptime = (DateTime.UtcNow - _startTime).ToString(@"dd\.hh\:mm\:ss"),
            RequestsPerSecond = _requestMetrics.GetRequestsPerSecond(),
            AverageResponseTimeMs = _requestMetrics.GetAverageResponseTimeMs(),
            ErrorRate = errorRate,
            // Request success rate over the tracked window (real, measured), not a
            // fabricated historical uptime percentage — there's no persistent
            // downtime tracker to compute true calendar-time availability from.
            Availability = 100m - errorRate
        });
    }

    /// <summary>
    /// Get recent error logs
    /// </summary>
    [HttpGet("logs/errors")]
    [Authorize]
    [RequirePermission(Permissions.PlatformAdmin)]
    [ProducesResponseType(typeof(List<ErrorLogDto>), StatusCodes.Status200OK)]
    public IActionResult GetRecentErrors([FromQuery] int limit = 50)
    {
        // Backed by the real rolling Serilog file sink (Program.cs: WriteTo.File("logs/qmgr-.log", ...))
        // rather than a dedicated error-log table/service, which this deployment doesn't have.
        var entries = new List<ErrorLogDto>();

        try
        {
            // Matches Serilog's own "logs/qmgr-.log" config in Program.cs, which resolves
            // relative to the content root (process working directory) — NOT the compiled
            // bin/ output directory that AppContext.BaseDirectory points to.
            var logDirectory = Path.Combine(_hostEnvironment.ContentRootPath, "logs");
            if (!Directory.Exists(logDirectory))
                return Ok(entries);

            // Default Serilog file-sink line format: "2026-08-17 14:53:12.345 +03:00 [ERR] message"
            var lineRegex = new Regex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<level>\w{3})\] (?<message>.*)$");

            var logFiles = Directory.GetFiles(logDirectory, "qmgr-*.log")
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                .Take(3); // today's file plus a couple of recent rolled-over ones is enough for "recent" errors

            foreach (var file in logFiles)
            {
                string content;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                }
                catch
                {
                    continue; // file locked by the active Serilog sink or otherwise unreadable — skip, don't fail the endpoint
                }

                ErrorLogDto? current = null;
                foreach (var rawLine in content.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length == 0) continue;

                    var match = lineRegex.Match(line);
                    if (match.Success)
                    {
                        if (current != null) entries.Add(current);
                        current = null;

                        var level = match.Groups["level"].Value;
                        if ((level == "ERR" || level == "FTL") && DateTimeOffset.TryParse(match.Groups["ts"].Value, out var ts))
                        {
                            current = new ErrorLogDto
                            {
                                Timestamp = ts.UtcDateTime,
                                Level = level == "FTL" ? "Fatal" : "Error",
                                Service = "Q-Mgr API",
                                Message = match.Groups["message"].Value
                            };
                        }
                    }
                    else if (current != null)
                    {
                        // Continuation line (exception stack trace) belonging to the current entry
                        current.StackTrace = current.StackTrace == null ? line : current.StackTrace + "\n" + line;
                    }
                }
                if (current != null) entries.Add(current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read recent errors from log files");
        }

        return Ok(entries.OrderByDescending(e => e.Timestamp).Take(limit).ToList());
    }

    /// <summary>
    /// No backup-tracking table or scheduled job exists in this deployment. If
    /// scripts/backup-database.ps1 has ever run successfully, it writes a UTC
    /// timestamp marker here — read it back honestly rather than fabricate a date.
    /// </summary>
    private DateTime? ReadLastBackupTimestamp()
    {
        try
        {
            var markerPath = Path.Combine(_hostEnvironment.ContentRootPath, "logs", "last-backup.marker");
            if (!System.IO.File.Exists(markerPath)) return null;

            var text = System.IO.File.ReadAllText(markerPath).Trim();
            return DateTimeOffset.TryParse(text, out var ts) ? ts.UtcDateTime : null;
        }
        catch
        {
            return null;
        }
    }
}

#region DTOs

public class SystemHealthDto
{
    public string OverallStatus { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Uptime { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DatabaseStatus { get; set; } = string.Empty;
    public string CacheStatus { get; set; } = string.Empty;
    public string QueueStatus { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
}

public class DatabaseHealthDto
{
    public string Status { get; set; } = string.Empty;
    public bool CanConnect { get; set; }
    public long ResponseTimeMs { get; set; }
    public int TotalOrganizations { get; set; }
    public int TotalUsers { get; set; }
    public int TotalTokens { get; set; }
    public string DatabaseProvider { get; set; } = string.Empty;
    public DateTime? LastBackup { get; set; }
}

public class ServiceHealthDto
{
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long ResponseTimeMs { get; set; }
    public DateTime LastChecked { get; set; }
}

public class PerformanceMetricsDto
{
    public long MemoryUsedMb { get; set; }
    public long GcMemoryMb { get; set; }
    public int ThreadCount { get; set; }
    public long CpuTimeSeconds { get; set; }
    public string Uptime { get; set; } = string.Empty;
    public decimal RequestsPerSecond { get; set; }
    public decimal AverageResponseTimeMs { get; set; }
    public decimal ErrorRate { get; set; }
    public decimal Availability { get; set; }
}

public class ErrorLogDto
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

#endregion
