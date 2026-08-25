using System.Net.Http.Json;
using System.Text.Json;

namespace QMgr.Web.Services;

/// <summary>
/// Service for extracting detailed error information from API responses
/// </summary>
public static class ApiErrorService
{
    /// <summary>
    /// Extracts a user-friendly error message from an HTTP response
    /// </summary>
    public static async Task<string> GetErrorMessageAsync(HttpResponseMessage response, string defaultMessage = "An error occurred")
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
                return defaultMessage;

            // Try to parse as ProblemDetails (RFC 7807)
            var problemDetails = JsonSerializer.Deserialize<ProblemDetailsDto>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (problemDetails != null)
            {
                // Prefer detail over title, but use title if detail is empty
                if (!string.IsNullOrWhiteSpace(problemDetails.Detail))
                    return problemDetails.Detail;

                if (!string.IsNullOrWhiteSpace(problemDetails.Title))
                    return problemDetails.Title;
            }

            // Try to parse as simple error object with message property
            var simpleError = JsonSerializer.Deserialize<SimpleErrorDto>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (!string.IsNullOrWhiteSpace(simpleError?.Message))
                return simpleError.Message;

            if (!string.IsNullOrWhiteSpace(simpleError?.Error))
                return simpleError.Error;

            // If we can't parse the JSON, return the raw content if it's short
            if (content.Length < 200)
                return content;

            return defaultMessage;
        }
        catch
        {
            return defaultMessage;
        }
    }

    /// <summary>
    /// Gets the error title from a ProblemDetails response
    /// </summary>
    public static async Task<(string Title, string Detail)> GetErrorDetailsAsync(HttpResponseMessage response)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
                return ("Error", "An error occurred");

            var problemDetails = JsonSerializer.Deserialize<ProblemDetailsDto>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (problemDetails != null)
            {
                return (
                    problemDetails.Title ?? "Error",
                    problemDetails.Detail ?? "An error occurred"
                );
            }

            return ("Error", content.Length < 200 ? content : "An error occurred");
        }
        catch
        {
            return ("Error", "An error occurred");
        }
    }

    private class ProblemDetailsDto
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public int? Status { get; set; }
        public string? Detail { get; set; }
        public string? Instance { get; set; }
    }

    private class SimpleErrorDto
    {
        public string? Message { get; set; }
        public string? Error { get; set; }
        public string? ErrorDescription { get; set; }
    }
}
