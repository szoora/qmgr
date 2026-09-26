using System.IO.Compression;
using System.Text;

namespace QMgr.Infrastructure.Services.Storage;

/// <summary>
/// THE SERVER'S OWN CHECK ON AN UPLOADED PDF (lesson plans plan §5.5, 2026-09-26). The browser rebuilds a plan's PDF
/// before it is sent — a courtesy, never a control — so the server looks again, with the base library alone
/// (<see cref="ZLibStream"/>; no PDF library, no server dependency):
/// <list type="bullet">
/// <item><c>%PDF-</c> at the start and <c>%%EOF</c> near the end;</item>
/// <item>every stream inflated (FlateDecode) and every NAME token read, with <c>#xx</c> escapes decoded, so
/// <c>/J#61vaScript</c> is read as <c>/JavaScript</c>;</item>
/// <item>refused: scripts and automatic actions (<c>/JavaScript /JS /OpenAction /AA /Launch</c>), payloads
/// (<c>/EmbeddedFile /RichMedia</c>), forms (<c>/AcroForm /XFA /SubmitForm /ImportData</c>) and remote go-tos — the
/// features PDFiD flags and ISO 32000-1 §12.6.4 defines;</item>
/// <item>an object stream that cannot be inflated is refused, because it could hide any of the above;</item>
/// <item>pages counted (<c>/Type /Page</c>), and inflation capped so a small file cannot expand without limit.</item>
/// </list>
/// The answer is a sentence a teacher can read, never a parser error.
/// </summary>
public static class PdfGate
{
    public sealed record Result(bool Ok, string? Problem, int Pages);

    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "JavaScript", "JS", "OpenAction", "AA", "Launch", "EmbeddedFile", "EmbeddedFiles", "RichMedia",
        "AcroForm", "XFA", "SubmitForm", "ImportData", "GoToR", "GoToE", "Sound", "Movie"
    };

    private const long MaxInflatedBytes = 40L * 1024 * 1024;

    public static Result Check(byte[] bytes, int maxPages)
    {
        if (bytes.Length < 64 || !(bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-'))
            return new(false, "That is not a PDF file.", 0);
        var tail = Encoding.Latin1.GetString(bytes, Math.Max(0, bytes.Length - 2048), Math.Min(2048, bytes.Length));
        if (!tail.Contains("%%EOF", StringComparison.Ordinal))
            return new(false, "The PDF is incomplete. Save it again and upload the new copy.", 0);

        var pages = 0;
        long inflatedTotal = 0;
        var text = Encoding.Latin1.GetString(bytes);

        // The body outside streams, then each stream's inflated contents.
        var scanProblem = ScanNames(text, ref pages, skipStreams: true);
        if (scanProblem != null) return new(false, scanProblem, pages);

        var at = 0;
        while (true)
        {
            var start = text.IndexOf("stream", at, StringComparison.Ordinal);
            if (start < 0) break;
            // "endstream" also contains "stream": skip it.
            if (start >= 3 && text.AsSpan(start - 3, 3).SequenceEqual("end")) { at = start + 6; continue; }
            var dataStart = start + 6;
            if (dataStart < text.Length && text[dataStart] == '\r') dataStart++;
            if (dataStart < text.Length && text[dataStart] == '\n') dataStart++;
            var end = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (end < 0) return new(false, "The PDF is damaged: a stream never ends.", pages);

            var dictStart = text.LastIndexOf("<<", start, Math.Min(start, 4096), StringComparison.Ordinal);
            var dict = dictStart >= 0 ? text[dictStart..start] : string.Empty;
            var isObjStm = dict.Contains("/ObjStm", StringComparison.Ordinal);
            var flate = dict.Contains("/FlateDecode", StringComparison.Ordinal) || dict.Contains("/Fl ", StringComparison.Ordinal) || dict.Contains("/Fl/", StringComparison.Ordinal);
            // Pictures are DCT/JPX/CCITT data: nothing to read inside them.
            var picture = dict.Contains("/DCTDecode", StringComparison.Ordinal) || dict.Contains("/JPXDecode", StringComparison.Ordinal)
                          || dict.Contains("/CCITTFaxDecode", StringComparison.Ordinal) || dict.Contains("/JBIG2Decode", StringComparison.Ordinal);

            if (flate && !picture)
            {
                var inflated = TryInflate(bytes, dataStart, end - dataStart, MaxInflatedBytes - inflatedTotal);
                if (inflated == null)
                {
                    if (isObjStm) return new(false, "The PDF hides part of itself where it cannot be checked. Save it again, or type the plan on the form.", pages);
                }
                else
                {
                    inflatedTotal += inflated.Length;
                    if (inflatedTotal >= MaxInflatedBytes) return new(false, "The PDF expands to far more than its size suggests, so it was refused.", pages);
                    // Content streams hold drawing operators, not dictionaries; only object streams and unknown ones are read for names.
                    if (isObjStm || !dict.Contains("/Length", StringComparison.Ordinal) || !LooksLikeContent(inflated))
                    {
                        var problem = ScanNames(Encoding.Latin1.GetString(inflated), ref pages, skipStreams: false);
                        if (problem != null) return new(false, problem, pages);
                    }
                }
            }
            else if (isObjStm)
                return new(false, "The PDF hides part of itself where it cannot be checked. Save it again, or type the plan on the form.", pages);

            at = end + 9;
        }

        if (pages == 0) return new(false, "The PDF has no pages.", 0);
        if (pages > maxPages) return new(false, $"The PDF has {pages} pages; the school allows {maxPages}.", pages);
        return new(true, null, pages);
    }

    /// <summary>A content stream is drawing operators; a dictionary-bearing stream starts with "&lt;&lt;" or an object number.</summary>
    private static bool LooksLikeContent(byte[] inflated)
    {
        var probe = Encoding.Latin1.GetString(inflated, 0, Math.Min(inflated.Length, 400));
        return !probe.Contains("<<", StringComparison.Ordinal);
    }

    private static byte[]? TryInflate(byte[] bytes, int offset, int length, long budget)
    {
        if (length <= 0 || budget <= 0) return null;
        try
        {
            using var input = new MemoryStream(bytes, offset, length, writable: false);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > budget) return output.ToArray();
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Reads every NAME token (decoding #xx), refuses a forbidden one, and counts /Type /Page.</summary>
    private static string? ScanNames(string text, ref int pages, bool skipStreams)
    {
        string? previous = null;
        var i = 0;
        while (i < text.Length)
        {
            if (skipStreams && text[i] == 's' && string.CompareOrdinal(text, i, "stream", 0, 6) == 0 && (i < 3 || string.CompareOrdinal(text, i - 3, "end", 0, 3) != 0))
            {
                var end = text.IndexOf("endstream", i + 6, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 9;
                continue;
            }
            if (text[i] != '/') { i++; continue; }
            var sb = new StringBuilder();
            var j = i + 1;
            while (j < text.Length && !IsDelimiter(text[j]))
            {
                if (text[j] == '#' && j + 2 < text.Length && IsHex(text[j + 1]) && IsHex(text[j + 2]))
                {
                    sb.Append((char)Convert.ToInt32(text.Substring(j + 1, 2), 16));
                    j += 3;
                }
                else sb.Append(text[j++]);
            }
            var name = sb.ToString();
            if (Forbidden.Contains(name))
                return name switch
                {
                    "JavaScript" or "JS" => "The PDF contains a script, so it was refused. Save it again from Word, or type the plan on the form.",
                    "OpenAction" or "AA" or "Launch" => "The PDF tries to do something when it is opened, so it was refused.",
                    "EmbeddedFile" or "EmbeddedFiles" or "RichMedia" or "Sound" or "Movie" => "The PDF carries other files inside it, so it was refused.",
                    "AcroForm" or "XFA" or "SubmitForm" or "ImportData" => "The PDF is a fillable form, so it was refused. Print it to PDF first, or type the plan on the form.",
                    _ => "The PDF links out to other documents in a way that is not allowed, so it was refused."
                };
            if (name == "Page" && previous == "Type") pages++;
            previous = name;
            i = j;
        }
        return null;
    }

    private static bool IsDelimiter(char c) => c is ' ' or '\r' or '\n' or '\t' or '\f' or '\0' or '/' or '<' or '>' or '[' or ']' or '(' or ')' or '{' or '}' or '%';
    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
