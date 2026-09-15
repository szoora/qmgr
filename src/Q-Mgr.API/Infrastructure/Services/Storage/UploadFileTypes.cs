namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// The one list of file types an upload may be, and how each is served.
///
/// Found by the 2026-09-15 security review of the gated-upload work: the upload gate checked the
/// CLIENT-DECLARED MIME type while the stored name kept the client's extension, and the serving
/// side chose its Content-Type from that extension — so `x.xml` declared as `image/png` was
/// accepted, stored as `.xml`, and served as `text/xml`, which browsers render and in which an
/// XHTML-namespaced `&lt;script&gt;` runs on this origin. The share endpoint likewise echoed the
/// stored MIME type verbatim. Both are the same defect: trusting the client about what a file IS.
///
/// The rule now: the extension a file is STORED under comes from this allow-list, keyed on the
/// declared type with the client's extension as a tie-break; anything else is refused at upload.
/// A `.pdf` must start with `%PDF-`. Serving inline is allowed only for the raster image, video,
/// audio and PDF types below; everything else is offered as a download.
/// </summary>
public static class UploadFileTypes
{
    /// <summary>Extension → the Content-Type it is served as. The only extensions a stored upload can have.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".pdf"] = "application/pdf",
    };

    /// <summary>Declared MIME type → the extension to store it under when the client's extension is not on the list.</summary>
    private static readonly Dictionary<string, string> ByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/ogg"] = ".ogg",
        ["audio/mp4"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["application/pdf"] = ".pdf",
    };

    /// <summary>
    /// The extension to store this upload under, or null when it is not a type this app accepts.
    /// The client's extension wins when it is on the list AND agrees with the declared type's
    /// family; otherwise the declared type decides; a type on neither list is refused.
    /// </summary>
    public static string? ResolveStoredExtension(string? fileName, string? declaredContentType)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        var mime = (declaredContentType ?? string.Empty).Split(';')[0].Trim();

        if (!string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var extMime))
        {
            // ".pdf" declared as image/png is a lie one way or the other; the extension's own
            // type is what it will be served as, so require the declaration to agree in family.
            if (string.IsNullOrEmpty(mime) || SameFamily(extMime, mime) || mime == "application/octet-stream")
                return ext.ToLowerInvariant();
        }

        if (ByMime.TryGetValue(mime, out var mimeExt))
            return mimeExt;

        return null;
    }

    private static bool SameFamily(string a, string b)
    {
        var fa = a.Split('/')[0];
        var fb = b.Split('/')[0];
        return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase) && (fa != "application" || string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Content-Type a stored file is served as, from its stored extension only. Null when the extension is not one of ours.</summary>
    public static string? ContentTypeFor(string storedFileName)
        => ByExtension.TryGetValue(Path.GetExtension(storedFileName), out var mime) ? mime : null;

    /// <summary>May this type be rendered inline by a browser on our origin? Everything else is a download.</summary>
    public static bool IsInlineSafe(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var mime = contentType.Split(';')[0].Trim();
        return ByExtension.Values.Contains(mime, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A PDF starts with "%PDF-". Anything stored as .pdf that does not is refused.</summary>
    public static bool HasPdfMagic(string diskPath)
    {
        try
        {
            using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[5];
            var read = fs.Read(head);
            return read == 5 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F' && head[4] == (byte)'-';
        }
        catch { return false; }
    }
}
