using System.Text.Json;

namespace QMgr.Web.Services;

/// <summary>
/// The one place a QMgr.API error response body gets turned into what a user actually sees. This
/// project had accumulated two competing implementations by the time this consolidation happened
/// (this one, used directly by ~11 Razor pages for a (title, detail) tuple; a second,
/// <c>ApiErrorMessage</c>, used inside the typed *ApiService classes for a single combined
/// string) — three others existed earlier in the same session and were already folded in here.
/// Every caller should go through this one instead of writing its own JsonDocument.Parse.
/// </summary>
public static class ApiErrorService
{
    /// <summary>
    /// A single combined string for callers that just need one message (typed *ApiService classes
    /// building an exception message their caller's catch block shows directly). Prefers
    /// "Title: Detail" when both are present and distinct, falls back to whichever exists, then to
    /// a per-field validation breakdown, then to the raw body, then to a generic fallback.
    /// </summary>
    public static async Task<string> GetErrorMessageAsync(HttpResponseMessage response, string defaultMessage = "An error occurred")
    {
        var body = await response.Content.ReadAsStringAsync();
        return FromBody(body, defaultMessage);
    }

    /// <summary>Same combined-message logic as <see cref="GetErrorMessageAsync"/>, for the one
    /// caller (QFileUpload.razor) that gets its error body from JS interop rather than a real
    /// HttpResponseMessage — a JS-driven upload never goes through HttpClient at all.</summary>
    public static string GetErrorMessageFromBody(string? body, string defaultMessage = "An error occurred") =>
        FromBody(body, defaultMessage);

    /// <summary>
    /// A (title, detail) pair for callers that show them as a toast's separate header/body (the
    /// established shape most Razor pages already use). Detail is empty — not a canned filler
    /// phrase — when the API genuinely didn't set one, so a toast with only a title doesn't get a
    /// misleading second line.
    /// </summary>
    public static async Task<(string Title, string Detail)> GetErrorDetailsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return DetailsFromBody(body);
    }

    private static string FromBody(string? body, string defaultMessage)
    {
        var (title, detail) = DetailsFromBody(body);
        if (string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(body) ? defaultMessage : (body!.Length <= 300 ? body : defaultMessage);

        return !string.IsNullOrWhiteSpace(detail) && !detail.Equals(title, StringComparison.OrdinalIgnoreCase)
            ? $"{title}: {detail}"
            : title;
    }

    private static (string Title, string Detail) DetailsFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ("", "");

        try
        {
            var root = JsonDocument.Parse(body).RootElement;

            // FluentValidation-backed endpoints (ExceptionHandlingMiddleware's ValidationException
            // case): a per-field breakdown is the most specific thing available, so it becomes the
            // detail (title stays whatever the API set, typically "Validation Error").
            string? fieldBreakdown = null;
            if (root.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Object)
            {
                var fieldMessages = errorsProp.EnumerateObject()
                    .Where(field => field.Value.ValueKind == JsonValueKind.Array)
                    .Select(field =>
                    {
                        var messages = field.Value.EnumerateArray()
                            .Select(m => m.GetString())
                            .Where(m => !string.IsNullOrWhiteSpace(m));
                        var joined = string.Join(", ", messages);
                        return string.IsNullOrEmpty(field.Name) ? joined : $"{field.Name}: {joined}";
                    })
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (fieldMessages.Count > 0) fieldBreakdown = string.Join("; ", fieldMessages);
            }

            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var detail = fieldBreakdown ?? (root.TryGetProperty("detail", out var detailProp) ? detailProp.GetString() : null);

            if (!string.IsNullOrWhiteSpace(title))
                return (title!, detail ?? "");

            // Older/simpler shapes a few endpoints still use: { "message": "..." } or { "error": "..." }.
            if (root.TryGetProperty("message", out var messageProp) && messageProp.GetString() is { Length: > 0 } message)
                return (message, "");
            if (root.TryGetProperty("error", out var errorProp) && errorProp.GetString() is { Length: > 0 } error)
                return (error, "");
        }
        catch (JsonException) { /* not JSON (e.g. an nginx/Kestrel error page) — fall back to raw body below */ }

        return (body.Length <= 200 ? body : "", "");
    }
}
