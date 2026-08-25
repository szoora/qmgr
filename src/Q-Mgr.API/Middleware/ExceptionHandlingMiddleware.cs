using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace QMgr.API.Middleware;

/// <summary>
/// Global exception handling middleware that converts exceptions to RFC 7807 ProblemDetails responses
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var response = context.Response;
        response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Instance = context.Request.Path
        };

        switch (exception)
        {
            case ValidationException validationException:
                problemDetails.Type = "https://qmgr.com/errors/validation";
                problemDetails.Title = "Validation Error";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = "One or more validation errors occurred.";
                problemDetails.Errors = validationException.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray()
                    );
                _logger.LogWarning("Validation error: {Errors}", JsonSerializer.Serialize(problemDetails.Errors));
                break;

            case UnauthorizedAccessException:
                problemDetails.Type = "https://qmgr.com/errors/unauthorized";
                problemDetails.Title = "Unauthorized";
                problemDetails.Status = (int)HttpStatusCode.Unauthorized;
                problemDetails.Detail = "You are not authorized to access this resource.";
                break;

            case KeyNotFoundException:
            case InvalidOperationException when exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase):
                problemDetails.Type = "https://qmgr.com/errors/not-found";
                problemDetails.Title = "Not Found";
                problemDetails.Status = (int)HttpStatusCode.NotFound;
                problemDetails.Detail = exception.Message;
                break;

            case DbUpdateConcurrencyException:
                problemDetails.Type = "https://qmgr.com/errors/concurrency";
                problemDetails.Title = "Concurrency Conflict";
                problemDetails.Status = (int)HttpStatusCode.Conflict;
                problemDetails.Detail = "The resource was modified by another user. Please refresh and try again.";
                _logger.LogWarning(exception, "Concurrency conflict occurred");
                break;

            case DbUpdateException dbEx when dbEx.InnerException is PostgresException pgEx:
                HandlePostgresException(pgEx, problemDetails);
                _logger.LogWarning(exception, "Database update error: {SqlState}", pgEx.SqlState);
                break;

            case DbUpdateException:
                problemDetails.Type = "https://qmgr.com/errors/database";
                problemDetails.Title = "Database Error";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = GetFriendlyDbErrorMessage(exception);
                _logger.LogWarning(exception, "Database update error");
                break;

            case ArgumentNullException:
            case ArgumentException:
                problemDetails.Type = "https://qmgr.com/errors/bad-request";
                problemDetails.Title = "Invalid Argument";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = exception.Message;
                _logger.LogWarning("Argument error: {Message}", exception.Message);
                break;

            case InvalidOperationException:
                problemDetails.Type = "https://qmgr.com/errors/bad-request";
                problemDetails.Title = "Bad Request";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = exception.Message;
                _logger.LogWarning("Bad request: {Message}", exception.Message);
                break;

            case TimeoutException:
                problemDetails.Type = "https://qmgr.com/errors/timeout";
                problemDetails.Title = "Request Timeout";
                problemDetails.Status = (int)HttpStatusCode.RequestTimeout;
                problemDetails.Detail = "The request took too long to process. Please try again.";
                _logger.LogWarning(exception, "Request timeout");
                break;

            case HttpRequestException httpEx:
                problemDetails.Type = "https://qmgr.com/errors/external-service";
                problemDetails.Title = "External Service Error";
                problemDetails.Status = (int)HttpStatusCode.BadGateway;
                problemDetails.Detail = "An error occurred while communicating with an external service.";
                _logger.LogError(httpEx, "External service error");
                break;

            default:
                problemDetails.Type = "https://qmgr.com/errors/internal";
                problemDetails.Title = "Internal Server Error";
                problemDetails.Status = (int)HttpStatusCode.InternalServerError;
                problemDetails.Detail = _environment.IsDevelopment()
                    ? exception.Message
                    : "An unexpected error occurred. Please try again later.";
                _logger.LogError(exception, "Unhandled exception occurred");
                break;
        }

        problemDetails.TraceId = context.TraceIdentifier;
        response.StatusCode = problemDetails.Status;

        // Include stack trace in development
        if (_environment.IsDevelopment() && problemDetails.Status == (int)HttpStatusCode.InternalServerError)
        {
            problemDetails.StackTrace = exception.StackTrace;
        }

        await response.WriteAsJsonAsync(problemDetails);
    }

    private static void HandlePostgresException(PostgresException pgEx, ProblemDetails problemDetails)
    {
        switch (pgEx.SqlState)
        {
            case "23505": // unique_violation
                problemDetails.Type = "https://qmgr.com/errors/duplicate";
                problemDetails.Title = "Duplicate Entry";
                problemDetails.Status = (int)HttpStatusCode.Conflict;
                problemDetails.Detail = ExtractDuplicateKeyMessage(pgEx) ?? "A record with this value already exists.";
                break;

            case "23503": // foreign_key_violation
                problemDetails.Type = "https://qmgr.com/errors/reference";
                problemDetails.Title = "Reference Error";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = "The operation failed because of a reference to a non-existent record.";
                break;

            case "23502": // not_null_violation
                problemDetails.Type = "https://qmgr.com/errors/validation";
                problemDetails.Title = "Missing Required Field";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = $"A required field is missing: {pgEx.ColumnName ?? "unknown"}";
                break;

            case "22001": // string_data_right_truncation
                problemDetails.Type = "https://qmgr.com/errors/validation";
                problemDetails.Title = "Value Too Long";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = "One of the provided values exceeds the maximum allowed length.";
                break;

            default:
                problemDetails.Type = "https://qmgr.com/errors/database";
                problemDetails.Title = "Database Error";
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Detail = "A database error occurred while processing your request.";
                break;
        }
    }

    private static string? ExtractDuplicateKeyMessage(PostgresException pgEx)
    {
        // PostgreSQL returns messages like: Key (column)=(value) already exists.
        var message = pgEx.Detail;
        if (string.IsNullOrEmpty(message)) return null;

        // Try to make the message more user-friendly
        if (message.Contains("Key (username)"))
            return "This username is already taken. Please choose a different username.";
        if (message.Contains("Key (email)"))
            return "This email address is already registered.";
        if (message.Contains("Key (code)"))
            return "This code is already in use. Please use a different code.";

        return $"A duplicate entry was found: {message}";
    }

    private static string GetFriendlyDbErrorMessage(Exception exception)
    {
        var innerMessage = exception.InnerException?.Message ?? exception.Message;

        // Make common database errors more user-friendly
        if (innerMessage.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            return "A record with this value already exists.";

        if (innerMessage.Contains("foreign key", StringComparison.OrdinalIgnoreCase))
            return "The operation failed because of a reference to a non-existent record.";

        if (innerMessage.Contains("not null", StringComparison.OrdinalIgnoreCase))
            return "A required field is missing.";

        return "A database error occurred while saving changes.";
    }
}

public class ProblemDetails
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Status { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string Instance { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public Dictionary<string, string[]>? Errors { get; set; }
    public string? StackTrace { get; set; }
}
