using System.Buffers.Binary;
using System.Text;

namespace QMgr.Application.Import.Documents;

/// <summary>
/// Reads a Word 97–2003 binary .doc with nothing but this file (decision D2): the [MS-CFB] compound file
/// that holds the streams, then the [MS-DOC] File Information Block → the <c>Clx</c> piece table for the
/// text, and the <c>PlcBtePapx</c> → PAPX FKPs for the paragraph properties that say where a table's
/// cells and rows end.
///
/// <para>How a table is found in the text. A cell ends with 0x07. So does a ROW — and the only thing that
/// tells the two apart is the paragraph property on that mark: <c>sprmPFTtp</c> (0x2417) set means
/// "table terminating paragraph", the end of a row. So every 0x07 is looked up in the PAPX runs by its file
/// position; a TTP mark closes the row, any other closes a cell. A paragraph mark (0x0D) inside a cell
/// (<c>sprmPFInTable</c> 0x2416) is a line break within it.</para>
///
/// <para>What it does not do: merged cells (they are in the row's TAP and add nothing for a programme a
/// school types into a plain grid), nested tables (read as their text), images and fields' codes (the
/// field RESULT is kept, the instruction between 0x13 and 0x14 is dropped). A file it cannot read is
/// refused with "save it as .docx", which every copy of Word can do.</para>
/// </summary>
public static class DocReader
{
    private const uint EndOfChain = 0xFFFFFFFA;
    private const ushort SprmPFInTable = 0x2416;
    private const ushort SprmPFTtp = 0x2417;
    private const ushort SprmPFInnerTtp = 0x244C;
    private const ushort SprmPItap = 0x6649;

    public static ImportDocument Read(string fileName, byte[] data)
    {
        try
        {
            return ReadCore(fileName, data);
        }
        catch (ImportDocumentException) { throw; }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            throw new ImportDocumentException($"\"{fileName}\" is an older Word document this reader could not follow. Open it in Word and save it as .docx, then choose that file.");
        }
    }

    private static ImportDocument ReadCore(string fileName, byte[] data)
    {
        var cfb = new CompoundFile(data);
        var wd = cfb.Stream("WordDocument")
                 ?? throw new ImportDocumentException($"\"{fileName}\" is not a Word document. Save it as .docx and try again.");

        var nFib = U16(wd, 0x02);
        if (nFib < 0x00C1)
            throw new ImportDocumentException($"\"{fileName}\" was saved by Word 95 or earlier. Open it in Word and save it as .docx.");
        var flags = U16(wd, 0x0A);
        if ((flags & 0x0100) != 0)
            throw new ImportDocumentException($"\"{fileName}\" is protected with a password. Save an unprotected copy and try again.");

        var table = cfb.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table")
                    ?? throw new ImportDocumentException($"\"{fileName}\" is missing part of its structure. Save it as .docx and try again.");

        // FIB: base (32 bytes), csw + FibRgW97, cslw + FibRgLw97, cbRgFcLcb + FibRgFcLcbBlob.
        var csw = U16(wd, 32);
        var pos = 34 + csw * 2;
        var cslw = U16(wd, pos);
        pos += 2 + cslw * 4;
        var fcLcbBase = pos + 2;
        (uint Fc, uint Lcb) FcLcb(int index) => (U32(wd, fcLcbBase + index * 8), U32(wd, fcLcbBase + index * 8 + 4));

        var (fcClx, lcbClx) = FcLcb(33);
        var (fcBte, lcbBte) = FcLcb(13);

        var chars = ReadText(wd, table, (int)fcClx, (int)lcbClx);
        var runs = ReadParagraphRuns(wd, table, (int)fcBte, (int)lcbBte);

        return Assemble(fileName, chars, runs);
    }

    // ---- Text ------------------------------------------------------------------------------------------

    private readonly record struct DocChar(char Ch, uint Fc);

    private static List<DocChar> ReadText(byte[] wd, byte[] table, int fcClx, int lcbClx)
    {
        var i = fcClx;
        var end = fcClx + lcbClx;
        // Skip Prc (property modifiers), each 0x01 + cb + grpprl.
        while (i < end && table[i] == 0x01) i += 3 + U16(table, i + 1);
        if (i >= end || table[i] != 0x02) throw new InvalidOperationException("No piece table");
        var lcb = (int)U32(table, i + 1);
        var plc = i + 5;
        var n = (lcb - 4) / 12;

        var chars = new List<DocChar>();
        for (var k = 0; k < n; k++)
        {
            var cpStart = U32(table, plc + k * 4);
            var cpEnd = U32(table, plc + (k + 1) * 4);
            var pcd = plc + (n + 1) * 4 + k * 8;
            var fc = U32(table, pcd + 2);
            var length = (int)(cpEnd - cpStart);
            if (length <= 0) continue;
            if ((fc & 0x40000000) != 0)
            {
                var offset = (int)((fc & 0x3FFFFFFF) / 2);
                for (var j = 0; j < length && offset + j < wd.Length; j++)
                    chars.Add(new DocChar(Cp1252(wd[offset + j]), (uint)(offset + j)));
            }
            else
            {
                var offset = (int)fc;
                for (var j = 0; j < length && offset + 2 * j + 1 < wd.Length; j++)
                    chars.Add(new DocChar((char)U16(wd, offset + 2 * j), (uint)(offset + 2 * j)));
            }
        }
        return chars;
    }

    /// <summary>A compressed piece is Windows-1252; only 0x80–0x9F differ from Latin-1.</summary>
    private static readonly ushort[] Cp1252High =
    {
        0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
        0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178
    };

    private static char Cp1252(byte b) => b is >= 0x80 and <= 0x9F ? (char)Cp1252High[b - 0x80] : (char)b;

    // ---- Paragraph properties -------------------------------------------------------------------------

    private readonly record struct ParaRun(uint FcStart, uint FcEnd, bool InTable, bool RowEnd, int Depth);

    private static List<ParaRun> ReadParagraphRuns(byte[] wd, byte[] table, int fcBte, int lcbBte)
    {
        var runs = new List<ParaRun>();
        if (lcbBte < 12) return runs;
        var m = (lcbBte - 4) / 8;
        for (var k = 0; k < m; k++)
        {
            var pn = U32(table, fcBte + (m + 1) * 4 + k * 4) & 0x3FFFFF;
            var page = (int)pn * 512;
            if (page + 512 > wd.Length) continue;
            int crun = wd[page + 511];
            for (var r = 0; r < crun; r++)
            {
                var fcStart = U32(wd, page + r * 4);
                var fcEnd = U32(wd, page + (r + 1) * 4);
                int bOffset = wd[page + (crun + 1) * 4 + r * 13];
                bool inTable = false, rowEnd = false;
                var depth = 0;
                if (bOffset != 0)
                {
                    var o = page + bOffset * 2;
                    int cb = wd[o];
                    int size;
                    if (cb == 0) { size = 2 * wd[o + 1]; o += 2; }
                    else { size = 2 * cb - 1; o += 1; }
                    // GrpPrlAndIstd: a 2-byte istd, then the sprms.
                    var q = o + 2;
                    var stop = Math.Min(o + size, page + 511);
                    while (q + 2 <= stop)
                    {
                        var sprm = U16(wd, q);
                        q += 2;
                        var operand = OperandSize(sprm, wd, q);
                        var value = q < stop ? wd[q] : (byte)0;
                        if (sprm == SprmPFInTable && value != 0) inTable = true;
                        else if ((sprm == SprmPFTtp || sprm == SprmPFInnerTtp) && value != 0) rowEnd = true;
                        else if (sprm == SprmPItap && q + 4 <= stop) depth = (int)U32(wd, q);
                        q += operand;
                    }
                }
                runs.Add(new ParaRun(fcStart, fcEnd, inTable || depth > 0, rowEnd, depth));
            }
        }
        runs.Sort((a, b) => a.FcStart.CompareTo(b.FcStart));
        return runs;
    }

    /// <summary>[MS-DOC] 2.2.5.1: the operand size is in the sprm's top three bits (spra).</summary>
    private static int OperandSize(ushort sprm, byte[] data, int at)
    {
        switch (sprm >> 13)
        {
            case 0: case 1: return 1;
            case 2: case 4: case 5: return 2;
            case 3: return 4;
            case 7: return 3;
            default:
                if (at >= data.Length) return 1;
                // sprmTDefTable and sprmPChgTabs carry a two-byte / special length.
                if (sprm == 0xD608) return at + 1 < data.Length ? U16(data, at) + 1 : 2;
                return data[at] + 1;
        }
    }

    private static ParaRun? RunAt(List<ParaRun> runs, uint fc)
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var r = runs[mid];
            if (fc < r.FcStart) hi = mid - 1;
            else if (fc >= r.FcEnd) lo = mid + 1;
            else return r;
        }
        return null;
    }

    // ---- Assembly --------------------------------------------------------------------------------------

    private static ImportDocument Assemble(string fileName, List<DocChar> chars, List<ParaRun> runs)
    {
        var doc = new ImportDocument { FileName = fileName, Format = "doc" };
        ImportDocTable? current = null;
        var row = new List<ImportDocCell>();
        var cellParas = new List<string>();
        var text = new StringBuilder();
        var fieldDepth = 0;
        var inInstruction = new Stack<bool>();

        void CloseTable()
        {
            if (current == null) return;
            if (row.Count > 0) { current.Rows.Add(row); row = new List<ImportDocCell>(); }
            doc.Tables.Add(current);
            current = null;
        }

        foreach (var c in chars)
        {
            var ch = c.Ch;
            // Fields: 0x13 begin, 0x14 separator, 0x15 end. The instruction is dropped, the result kept.
            if (ch == '\x13') { fieldDepth++; inInstruction.Push(true); continue; }
            if (ch == '\x14') { if (inInstruction.Count > 0) { inInstruction.Pop(); inInstruction.Push(false); } continue; }
            if (ch == '\x15') { if (fieldDepth > 0) fieldDepth--; if (inInstruction.Count > 0) inInstruction.Pop(); continue; }
            if (inInstruction.Count > 0 && inInstruction.Peek()) continue;

            if (ch == '\x07')
            {
                var run = RunAt(runs, c.Fc);
                if (run is { RowEnd: true })
                {
                    // Anything typed after the last cell mark of the row is not a cell.
                    text.Clear();
                    cellParas.Clear();
                    current ??= new ImportDocTable();
                    current.Rows.Add(row);
                    row = new List<ImportDocCell>();
                }
                else
                {
                    cellParas.Add(text.ToString());
                    text.Clear();
                    // A cell mark outside any table run still ends a cell: some writers set InTable only on the row.
                    if (current == null)
                    {
                        current = new ImportDocTable();
                    }
                    row.Add(new ImportDocCell { Text = ImportDocText.JoinParagraphs(cellParas) });
                    cellParas.Clear();
                }
                continue;
            }

            if (ch == '\r')
            {
                var run = RunAt(runs, c.Fc);
                if (run is { InTable: true })
                {
                    cellParas.Add(text.ToString());
                    text.Clear();
                }
                else
                {
                    CloseTable();
                    var p = ImportDocText.Tidy(text.ToString());
                    if (p.Length > 0) doc.Paragraphs.Add(new ImportDocParagraph { Text = p, BeforeTable = doc.Tables.Count });
                    text.Clear();
                }
                continue;
            }

            switch (ch)
            {
                case '\x0B': text.Append(" / "); break;          // manual line break
                case '\x0C': case '\x0E': text.Append(' '); break; // page / column break
                case '\x1E': text.Append('-'); break;           // non-breaking hyphen
                case '\x1F': break;                             // optional hyphen
                case '\x01': case '\x08': case '\x05': case '\x02': case '\x03': case '\x04': break; // objects, footnote refs, annotations
                case '\t': text.Append(' '); break;
                default:
                    if (!char.IsControl(ch)) text.Append(ch);
                    break;
            }
        }

        if (text.Length > 0 && current == null)
        {
            var p = ImportDocText.Tidy(text.ToString());
            if (p.Length > 0) doc.Paragraphs.Add(new ImportDocParagraph { Text = p, BeforeTable = doc.Tables.Count });
        }
        CloseTable();
        return doc;
    }

    // ---- [MS-CFB] ----------------------------------------------------------------------------------------

    private sealed class CompoundFile
    {
        private readonly byte[] _data;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly List<uint> _fat = new();
        private readonly List<uint> _miniFat = new();
        private readonly Dictionary<string, (uint Start, ulong Size)> _entries = new(StringComparer.Ordinal);
        private readonly byte[] _miniStream;

        public CompoundFile(byte[] data)
        {
            _data = data;
            if (data.Length < 512 || U32(data, 0) != 0xE011CFD0 || U32(data, 4) != 0xE11AB1A1)
                throw new ImportDocumentException("That file is not a Word 97–2003 document.");
            _sectorSize = 1 << U16(data, 0x1E);
            _miniSectorSize = 1 << U16(data, 0x20);
            var fatSectors = U32(data, 0x2C);
            var dirStart = U32(data, 0x30);
            _miniCutoff = U32(data, 0x38);
            var miniFatStart = U32(data, 0x3C);
            var miniFatCount = U32(data, 0x40);
            var difatStart = U32(data, 0x44);
            var difatCount = U32(data, 0x48);

            var difat = new List<uint>();
            for (var i = 0; i < 109; i++) difat.Add(U32(data, 0x4C + i * 4));
            var d = difatStart;
            for (var k = 0; k < difatCount && d < EndOfChain; k++)
            {
                var off = SectorOffset(d);
                var per = _sectorSize / 4 - 1;
                for (var i = 0; i < per; i++) difat.Add(U32(data, off + i * 4));
                d = U32(data, off + per * 4);
            }
            for (var i = 0; i < fatSectors && i < difat.Count; i++)
            {
                var off = SectorOffset(difat[i]);
                for (var j = 0; j < _sectorSize / 4; j++) _fat.Add(U32(data, off + j * 4));
            }

            var dir = Chain(dirStart, ulong.MaxValue);
            for (var i = 0; i + 128 <= dir.Length; i += 128)
            {
                var nameLength = U16(dir, i + 64);
                if (nameLength < 2) continue;
                var name = Encoding.Unicode.GetString(dir, i, Math.Min(64, (int)nameLength) - 2);
                var start = U32(dir, i + 116);
                var size = BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(i + 120, 8));
                // Version 3 files keep only the low 32 bits meaningful.
                if (_sectorSize == 512) size &= 0xFFFFFFFF;
                _entries.TryAdd(name, (start, size));
            }

            if (miniFatCount > 0 && miniFatStart < EndOfChain)
            {
                var mf = Chain(miniFatStart, ulong.MaxValue);
                for (var i = 0; i + 4 <= mf.Length; i += 4) _miniFat.Add(U32(mf, i));
            }
            _miniStream = _entries.TryGetValue("Root Entry", out var root) ? Chain(root.Start, root.Size) : Array.Empty<byte>();
        }

        private int SectorOffset(uint sector)
        {
            var off = (long)(sector + 1) * _sectorSize;
            if (off + _sectorSize > _data.Length) throw new InvalidOperationException("Sector out of range");
            return (int)off;
        }

        private byte[] Chain(uint start, ulong size)
        {
            using var ms = new MemoryStream();
            var s = start;
            var guard = 0;
            while (s < EndOfChain && guard++ < 1_000_000 && (ulong)ms.Length < size)
            {
                ms.Write(_data, SectorOffset(s), _sectorSize);
                if (s >= _fat.Count) break;
                s = _fat[(int)s];
            }
            var bytes = ms.ToArray();
            return size < (ulong)bytes.Length ? bytes[..(int)size] : bytes;
        }

        public byte[]? Stream(string name)
        {
            if (!_entries.TryGetValue(name, out var e)) return null;
            if (e.Size >= _miniCutoff) return Chain(e.Start, e.Size);
            using var ms = new MemoryStream();
            var s = e.Start;
            var guard = 0;
            while (s < EndOfChain && guard++ < 1_000_000 && (ulong)ms.Length < e.Size)
            {
                var off = (int)s * _miniSectorSize;
                if (off + _miniSectorSize > _miniStream.Length) break;
                ms.Write(_miniStream, off, _miniSectorSize);
                if (s >= _miniFat.Count) break;
                s = _miniFat[(int)s];
            }
            var bytes = ms.ToArray();
            return e.Size < (ulong)bytes.Length ? bytes[..(int)e.Size] : bytes;
        }
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
}
