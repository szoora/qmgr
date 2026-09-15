using QMgr.Application.Interfaces;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// Static face of <see cref="IUploadAccessService"/> for the DTO mappers that are themselves
/// static (<c>VisitorsController.MapToDto</c>, <c>StudentsController.MapToDto</c> — reused by
/// jobs and reporting, which is why they are static). The instance is the DI singleton, attached
/// once at startup in Program.cs; until it is, <see cref="Sign"/> returns the link unchanged, so
/// nothing can throw for want of it.
///
/// A gated link (a visitor or student photograph, welfare evidence) is only useful to a browser
/// with a token on it, so every DTO that carries one signs it here; and every write that ACCEPTS
/// one back from a client strips it here, so a token never gets persisted.
/// </summary>
public static class UploadLinks
{
    private static IUploadAccessService? _service;

    public static void Use(IUploadAccessService service) => _service = service;

    /// <summary>Signs one of our own upload links for the default lifetime; anything else passes through untouched.</summary>
    public static string? Sign(string? url) => _service == null ? url : _service.Sign(url);

    /// <summary>Removes an access token before a link is stored.</summary>
    public static string? Strip(string? url) => _service == null ? url : _service.Strip(url);

    private static readonly System.Text.RegularExpressions.Regex TokenInText = new(
        @"(/" + UploadAccessService.RelativeFolder + @"/[^""'\s?<>]+)\?" + IUploadAccessService.TokenQueryKey + @"=[^""'\s&<>]*(&?)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes access tokens from every one of our upload links inside a block of text — an
    /// article body whose editor inserted images from their signed preview links.
    /// </summary>
    public static string? StripAll(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(UploadAccessService.RelativeFolder, StringComparison.OrdinalIgnoreCase)) return text;
        return TokenInText.Replace(text, m => m.Groups[1].Value + (m.Groups[2].Value == "&" ? "?" : ""));
    }
}
