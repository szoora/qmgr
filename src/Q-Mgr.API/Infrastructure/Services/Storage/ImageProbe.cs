namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// What an image actually IS, read from its own first bytes — and how big it is — for PNG, JPEG
/// and WebP. Nothing else in this codebase could answer either question: <see cref="UploadFileTypes"/>
/// checks magic bytes for PDF only (<c>%PDF-</c>) and takes every image on trust.
///
/// OWASP's File Upload Cheat Sheet is the reference and every rule it states already has a
/// counterpart here — validate by content and not by extension or the client's Content-Type,
/// generate the stored name, cap the size, store outside the webroot. This closes the one that
/// did not: a <c>.png</c> whose bytes are a script was accepted and stored as <c>.png</c>, which
/// the serving side then labels <c>image/png</c>. That is not directly executable, but a logo is
/// rendered by every screen in the app and a sign-in page is anonymous, so "the bytes are what
/// the name says" is worth proving rather than assuming.
///
/// DELIBERATELY DEPENDENCY-FREE. There is no image library on this server and there is not going
/// to be one (the standing no-third-party-server-dependencies rule; the same rule that keeps PPTX
/// rendering and a PDF renderer out). Three container formats' headers are about a hundred lines
/// of well-specified parsing, and that is cheaper than a package.
///
/// SVG IS NOT HERE, ON PURPOSE. OWASP: SVG allows ECMAScript in almost every context, and its
/// advice if you must accept it is to serve it as <c>text/plain</c> or from a separate content
/// domain. A logo served as text/plain is not a logo, and a separate content domain is a whole
/// project. A favicon does not need SVG — a 512x512 PNG is what every browser accepts.
/// </summary>
public static class ImageProbe
{
    public sealed record ImageInfo(string ContentType, int Width, int Height);

    /// <summary>
    /// The image this file really is, or null when its bytes are not a PNG, JPEG or WebP.
    /// The caller compares <see cref="ImageInfo.ContentType"/> against the extension the file was
    /// about to be STORED under; a disagreement is a refusal, never a rename.
    /// </summary>
    public static ImageInfo? Read(string diskPath)
    {
        try
        {
            using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(fs);
        }
        catch
        {
            return null;
        }
    }

    public static ImageInfo? Read(Stream stream)
    {
        if (!stream.CanSeek) return null;
        stream.Position = 0;

        Span<byte> head = stackalloc byte[32];
        var read = stream.Read(head);
        if (read < 16) return null;

        if (IsPng(head)) return ReadPng(stream);
        if (head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(stream);
        if (IsRiffWebp(head)) return ReadWebp(stream);
        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> h) =>
        h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 &&
        h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A;

    private static bool IsRiffWebp(ReadOnlySpan<byte> h) =>
        h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F' &&
        h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P';

    /// <summary>PNG: the IHDR chunk is required to come first, so width/height sit at a fixed offset.</summary>
    private static ImageInfo? ReadPng(Stream stream)
    {
        stream.Position = 16;
        Span<byte> dim = stackalloc byte[8];
        if (stream.Read(dim) != 8) return null;
        var width = BigEndian32(dim[..4]);
        var height = BigEndian32(dim[4..]);
        return width > 0 && height > 0 ? new ImageInfo("image/png", width, height) : null;
    }

    /// <summary>
    /// JPEG: walk the segment markers to the start-of-frame, which is the only one carrying the
    /// dimensions. SOF4 (0xC4), SOF8 (0xC8) and SOF12 (0xCC) are NOT frames — they are the Huffman
    /// table, a reserved marker and the arithmetic-coding table — which is why the ranges skip them.
    /// </summary>
    private static ImageInfo? ReadJpeg(Stream stream)
    {
        stream.Position = 2;
        Span<byte> two = stackalloc byte[2];

        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue;               // resynchronise on the marker prefix
            while (b == 0xFF) b = stream.ReadByte(); // fill bytes are allowed before a marker
            if (b < 0) return null;

            var marker = (byte)b;
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;                          // standalone markers carry no length
            if (marker == 0xD9 || marker == 0xDA)
                return null;                       // end of image / start of scan: no frame header found

            if (stream.Read(two) != 2) return null;
            var length = (two[0] << 8) | two[1];
            if (length < 2) return null;

            var isFrame = (marker >= 0xC0 && marker <= 0xC3)
                       || (marker >= 0xC5 && marker <= 0xC7)
                       || (marker >= 0xC9 && marker <= 0xCB)
                       || (marker >= 0xCD && marker <= 0xCF);

            if (isFrame)
            {
                Span<byte> frame = stackalloc byte[5];
                if (stream.Read(frame) != 5) return null;
                var height = (frame[1] << 8) | frame[2];
                var width = (frame[3] << 8) | frame[4];
                return width > 0 && height > 0 ? new ImageInfo("image/jpeg", width, height) : null;
            }

            stream.Position += length - 2;
        }
    }

    /// <summary>WebP has three shapes: lossy (VP8 ), lossless (VP8L) and extended (VP8X).</summary>
    private static ImageInfo? ReadWebp(Stream stream)
    {
        stream.Position = 12;
        Span<byte> chunk = stackalloc byte[8];
        if (stream.Read(chunk) != 8) return null;
        var fourcc = System.Text.Encoding.ASCII.GetString(chunk[..4]);

        switch (fourcc)
        {
            case "VP8 ":
            {
                // Simple lossy: 3-byte frame tag, a 3-byte start code, then 14-bit width/height.
                Span<byte> body = stackalloc byte[10];
                if (stream.Read(body) != 10) return null;
                if (body[3] != 0x9D || body[4] != 0x01 || body[5] != 0x2A) return null;
                var w = ((body[7] << 8) | body[6]) & 0x3FFF;
                var h = ((body[9] << 8) | body[8]) & 0x3FFF;
                return w > 0 && h > 0 ? new ImageInfo("image/webp", w, h) : null;
            }
            case "VP8L":
            {
                Span<byte> body = stackalloc byte[5];
                if (stream.Read(body) != 5) return null;
                if (body[0] != 0x2F) return null;
                var bits = (uint)(body[1] | (body[2] << 8) | (body[3] << 16) | (body[4] << 24));
                var w = (int)(bits & 0x3FFF) + 1;
                var h = (int)((bits >> 14) & 0x3FFF) + 1;
                return new ImageInfo("image/webp", w, h);
            }
            case "VP8X":
            {
                // Extended: a flags byte, three reserved, then canvas width-1 and height-1 as 24-bit LE.
                Span<byte> body = stackalloc byte[10];
                if (stream.Read(body) != 10) return null;
                var w = (body[4] | (body[5] << 8) | (body[6] << 16)) + 1;
                var h = (body[7] | (body[8] << 8) | (body[9] << 16)) + 1;
                return new ImageInfo("image/webp", w, h);
            }
            default:
                return null;
        }
    }

    private static int BigEndian32(ReadOnlySpan<byte> b) => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
}
