using System.Globalization;
using System.Text;

namespace Aurora86.Core.Devices.Parallel;

/// <summary>
/// Renders a plain-text print job as a PDF, so that a report a DOS program
/// sent to LPT1 can be opened on the host without emulating a single printer
/// language (docs/PARALLEL-PORT.md section 3).
///
/// "Plain text" means the job never asked the printer to do anything a
/// teletype could not: printable characters, the DOS/CP437 accented and
/// symbol characters in the top half of the byte, and the five control
/// codes a line printer of the era actually obeyed -- CR, LF, FF, TAB and
/// BS. Anything else -- an ESC byte above all, the start of every printer
/// control language this class does not speak -- disqualifies the whole
/// job; <see cref="Render"/> is only ever called once <see cref="IsPlainText"/>
/// has said yes. A single trailing Ctrl-Z (1Ah), the DOS end-of-file marker
/// `COPY /A` and plenty of period editors append, is dropped before either
/// check looks at the job, since it was never meant to be printed. A job
/// that would need more than <see cref="MaxPages"/> pages is refused too --
/// not because a PDF could not hold that many, but because nothing sends a
/// legitimate print job that large, and a guest doing something else
/// (spraying binary data at LPT1 with the high bit clear, say) should get
/// only the raw `.prn`, not minutes spent laying out a PDF no one asked for.
///
/// The page this class lays out is A4 (595 by 842 PostScript points), with
/// 36-point margins top and bottom, and exactly 66 lines to a page -- the
/// leading is derived from that margin and line count rather than fixed, so
/// the sixty-sixth line's baseline lands precisely on the bottom margin.
/// Courier is normally set at 10 points, but a job whose widest line would
/// not fit the 523-point text width at that size is set smaller instead (see
/// <see cref="FitFontSize"/>), never below <see cref="MinimumFontSizePoints"/>
/// -- the leading, and so the 66 lines per page, do not change either way.
/// This class hand-builds the PDF 1.4 byte stream itself (catalog, page
/// tree, one font object, one content stream per page, cross-reference
/// table and trailer) because the job is small and fixed in shape enough
/// that a general-purpose PDF library would answer for far more than this
/// ever needs.
/// </summary>
public static class PlainTextPdf
{
    // The five control codes a plain-text job is allowed to use; see the
    // class summary. Every other byte below 0x20, and 0x7F, are printer
    // control sequences this class refuses to guess at.
    private const byte Backspace = 0x08;
    private const byte Tab = 0x09;
    private const byte LineFeed = 0x0A;
    private const byte FormFeed = 0x0C;
    private const byte CarriageReturn = 0x0D;

    /// <summary>DOS's own end-of-file marker (Ctrl-Z); see the class summary.</summary>
    private const byte EndOfFile = 0x1A;

    private const int TabStopColumns = 8;
    private const int LinesPerPage = 66;

    /// <summary>
    /// A job needing more pages than this is refused by <see cref="IsPlainText"/>
    /// before any page is laid out (*choice*: no period DOS report ran this
    /// long; this exists purely as a host-side bound on the work
    /// <see cref="Render"/> would otherwise do).
    /// </summary>
    private const int MaxPages = 2000;

    // The page geometry docs/PARALLEL-PORT.md section 3 fixes: A4 in
    // PostScript points, 36-point margins top and bottom.
    private const int PageWidthPoints = 595;
    private const int PageHeightPoints = 842;
    private const int MarginPoints = 36;

    /// <summary>Courier's advance width is a fixed fraction of its point size, the same for every glyph.</summary>
    private const double CourierAdvanceRatio = 0.6;

    private const double DefaultFontSizePoints = 10.0;
    private const double MinimumFontSizePoints = 5.0;

    /// <summary>
    /// True when every byte of <paramref name="job"/>, apart from one
    /// trailing Ctrl-Z, is something a plain text job is allowed to
    /// contain, and the job does not need more than <see cref="MaxPages"/>
    /// pages to lay out. An empty job (or one that is only a trailing
    /// Ctrl-Z) is not plain text -- there is nothing to typeset, so
    /// <see cref="FolderPrintJobSink"/> writes no PDF for it.
    /// </summary>
    public static bool IsPlainText(ReadOnlySpan<byte> job)
    {
        ReadOnlySpan<byte> content = TrimTrailingEndOfFile(job);
        if (content.IsEmpty)
        {
            return false;
        }

        // A single pass counts pages the same way LayoutPages does -- FF
        // and a 66th line both start a new one -- without allocating any of
        // them, so a job that turns out to need too many pages costs no
        // more to reject than one that does not.
        int pageCount = 1;
        int row = 0;

        foreach (byte value in content)
        {
            if (!IsAllowedByte(value))
            {
                return false;
            }

            if (value == FormFeed)
            {
                pageCount++;
                row = 0;
            }
            else if (value == LineFeed)
            {
                row++;
                if (row >= LinesPerPage)
                {
                    pageCount++;
                    row = 0;
                }
            }

            if (pageCount > MaxPages)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedByte(byte value)
    {
        // Ordinary printable ASCII.
        if (value is >= 0x20 and <= 0x7E)
        {
            return true;
        }

        // CP437's top half: always some printable glyph on a period DOS
        // screen or printer, whether or not WinAnsi has an equivalent for
        // it -- Render falls back to '?' for the ones it does not.
        if (value >= 0x80)
        {
            return true;
        }

        return value is CarriageReturn or LineFeed or FormFeed or Tab or Backspace;
    }

    /// <summary>
    /// Drops one trailing Ctrl-Z -- see the class summary -- before either
    /// check looks at the job. A Ctrl-Z anywhere else in the job is not
    /// touched, and disqualifies the job exactly as any other control code
    /// outside the allowed set would.
    /// </summary>
    private static ReadOnlySpan<byte> TrimTrailingEndOfFile(ReadOnlySpan<byte> job)
        => !job.IsEmpty && job[^1] == EndOfFile ? job[..^1] : job;

    /// <summary>
    /// Lays a plain-text job out onto A4 pages and returns a complete PDF
    /// 1.4 file. Callers are expected to have checked <see cref="IsPlainText"/>
    /// first; bytes this class does not otherwise understand are rendered
    /// as '?' rather than thrown on, so a caller that skips the check still
    /// gets a file rather than an exception.
    /// </summary>
    public static byte[] Render(ReadOnlySpan<byte> job)
    {
        ReadOnlySpan<byte> content = TrimTrailingEndOfFile(job);
        List<char>?[][] pages = LayoutPages(content);
        return BuildPdf(pages);
    }

    // --- laying the job out into pages and lines --------------------------

    /// <summary>
    /// Walks the job byte by byte, applying FF/CR/LF/TAB/BS the way
    /// docs/PARALLEL-PORT.md section 3 describes, and returns the finished
    /// pages. Each page is <see cref="LinesPerPage"/> line slots, left null
    /// until a character actually lands on that line -- a short job does
    /// not pay for 66 growable buffers it never writes to. A line that is
    /// written to is a growable list of characters, so that CR followed by
    /// more text overstrikes what was already there, the way a daisy-wheel
    /// or dot-matrix printer's carriage return actually behaves.
    /// </summary>
    private static List<char>?[][] LayoutPages(ReadOnlySpan<byte> job)
    {
        var pages = new List<List<char>?[]>();
        List<char>?[] page = NewPage();
        int row = 0;
        int column = 0;
        bool pageHasText = false;

        void StartNewPage()
        {
            pages.Add(page);
            page = NewPage();
            row = 0;
            column = 0;
            pageHasText = false;
        }

        foreach (byte value in job)
        {
            switch (value)
            {
                case FormFeed:
                    StartNewPage();
                    break;

                case CarriageReturn:
                    column = 0;
                    break;

                case LineFeed:
                    row++;
                    if (row >= LinesPerPage)
                    {
                        StartNewPage();
                    }
                    break;

                case Tab:
                    column = (column / TabStopColumns + 1) * TabStopColumns;
                    break;

                case Backspace:
                    if (column > 0)
                    {
                        column--;
                    }
                    break;

                default:
                    PlaceCharacter(page, row, column, MapToWinAnsi(value));
                    column++;
                    pageHasText = true;
                    break;
            }
        }

        // A form feed at the end of a job ejects the last sheet; it does not
        // start a blank one. Only a page that received text is kept, unless
        // the job had nothing at all to show.
        if (pageHasText || pages.Count == 0)
        {
            pages.Add(page);
        }

        return pages.ToArray();
    }

    private static List<char>?[] NewPage() => new List<char>?[LinesPerPage];

    /// <summary>
    /// Writes one character at a column, padding with spaces if the cursor
    /// (moved there by TAB, or simply never having reached that far) is
    /// past the end of what has been written on this line so far. The
    /// line's buffer is created here, on first use, rather than when the
    /// page itself is.
    /// </summary>
    /// <summary>
    /// The widest line laid out. At the 5 pt floor 174 Courier columns fit
    /// the page; anything past this is off the paper anyway, and the cap
    /// keeps a job of nothing but TABs from growing one line to hundreds of
    /// millions of spaces on the emulation thread.
    /// </summary>
    private const int MaximumColumns = 256;

    private static void PlaceCharacter(List<char>?[] page, int row, int column, char character)
    {
        if (column >= MaximumColumns)
        {
            return;
        }

        List<char> line = page[row] ??= new List<char>();
        while (line.Count <= column)
        {
            line.Add(' ');
        }
        line[column] = character;
    }

    // --- CP437 to WinAnsi -------------------------------------------------

    /// <summary>
    /// CP437's top half (80h-FFh), mapped to the byte WinAnsiEncoding gives
    /// the same character, or '?' where WinAnsi has no glyph for it at all
    /// -- the box-drawing and block characters above all, and the Greek
    /// letters and math symbols DOS used for the same purposes a modern
    /// document would use Unicode for. Index 0 is byte 0x80.
    /// </summary>
    private static readonly char[] Cp437HighHalfToWinAnsi =
    [
        (char)0xC7, (char)0xFC, (char)0xE9, (char)0xE2, (char)0xE4, (char)0xE0, (char)0xE5, (char)0xE7, // 80-87: Ç ü é â ä à å ç
        (char)0xEA, (char)0xEB, (char)0xE8, (char)0xEF, (char)0xEE, (char)0xEC, (char)0xC4, (char)0xC5, // 88-8F: ê ë è ï î ì Ä Å
        (char)0xC9, (char)0xE6, (char)0xC6, (char)0xF4, (char)0xF6, (char)0xF2, (char)0xFB, (char)0xF9, // 90-97: É æ Æ ô ö ò û ù
        (char)0xFF, (char)0xD6, (char)0xDC, (char)0xA2, (char)0xA3, (char)0xA5, '?', (char)0x83,       // 98-9F: ÿ Ö Ü ¢ £ ¥ ₧(none) ƒ
        (char)0xE1, (char)0xED, (char)0xF3, (char)0xFA, (char)0xF1, (char)0xD1, (char)0xAA, (char)0xBA, // A0-A7: á í ó ú ñ Ñ ª º
        (char)0xBF, '?', (char)0xAC, (char)0xBD, (char)0xBC, (char)0xA1, (char)0xAB, (char)0xBB,         // A8-AF: ¿ ⌐(none) ¬ ½ ¼ ¡ « »
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // B0-B7: shading/box drawing
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // B8-BF: box drawing
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // C0-C7: box drawing
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // C8-CF: box drawing
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // D0-D7: box drawing
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // D8-DF: box drawing / block elements
        '?', (char)0xDF, '?', '?', '?', '?', (char)0xB5, '?',                                            // E0-E7: α ß(mapped) Γ π Σ σ µ(mapped) τ
        '?', '?', '?', '?', '?', '?', '?', '?',                                                          // E8-EF: Φ Θ Ω δ ∞ φ ε ∩
        '?', (char)0xB1, '?', '?', '?', '?', (char)0xF7, '?',                                            // F0-F7: ≡ ±(mapped) ≥ ≤ ⌠ ⌡ ÷(mapped) ≈
        (char)0xB0, '?', (char)0xB7, '?', '?', (char)0xB2, '?', (char)0xA0,                               // F8-FF: °(mapped) ∙ ·(mapped) √ ⁿ ²(mapped) ■ nbsp(mapped)
    ];

    /// <summary>
    /// Maps one byte to the character WinAnsiEncoding must draw it as.
    /// Anything the caller has not already excluded through
    /// <see cref="IsAllowedByte"/> -- an ESC byte, say, reached only if a
    /// caller skipped <see cref="IsPlainText"/> -- draws as '?' rather than
    /// failing, since this method has to return something.
    /// </summary>
    private static char MapToWinAnsi(byte value)
    {
        if (value is >= 0x20 and <= 0x7E)
        {
            return (char)value;
        }

        if (value >= 0x80)
        {
            return Cp437HighHalfToWinAnsi[value - 0x80];
        }

        return '?';
    }

    // --- PDF construction --------------------------------------------------

    /// <summary>
    /// Assembles the complete PDF byte stream: catalog, page tree, one
    /// Courier/WinAnsi font object shared by every page, one content stream
    /// per page, and a cross-reference table whose offsets are measured as
    /// the file is written rather than computed separately, so they cannot
    /// drift out of step with the bytes they point at.
    /// </summary>
    private static byte[] BuildPdf(List<char>?[][] pages)
    {
        var buffer = new List<byte>();

        void Emit(string text) => buffer.AddRange(Encoding.Latin1.GetBytes(text));
        void EmitBytes(byte[] bytes) => buffer.AddRange(bytes);

        int pageCount = pages.Length;

        // Object numbering: catalog, page tree and font come first and
        // never move; pages and their content streams follow in matching
        // order, so object N's content stream is always object N + pageCount.
        const int CatalogObject = 1;
        const int PagesObject = 2;
        const int FontObject = 3;
        const int FirstPageObject = 4;
        int firstContentObject = FirstPageObject + pageCount;
        int totalObjects = firstContentObject + pageCount - 1;

        var offsets = new long[totalObjects + 1];   // 1-based; index 0 unused

        Emit("%PDF-1.4\n");

        offsets[CatalogObject] = buffer.Count;
        Emit($"{CatalogObject} 0 obj\n<< /Type /Catalog /Pages {PagesObject} 0 R >>\nendobj\n");

        offsets[PagesObject] = buffer.Count;
        string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(index => $"{FirstPageObject + index} 0 R"));
        Emit($"{PagesObject} 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        offsets[FontObject] = buffer.Count;
        Emit($"{FontObject} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>\nendobj\n");

        // The leading that makes the sixty-sixth line's baseline land
        // exactly on the bottom margin: with the first line's baseline one
        // leading below the top margin, line N's baseline is
        // (PageHeight - Margin - N * Leading), which equals the bottom
        // margin exactly when N = LinesPerPage and Leading divides the
        // margin-to-margin span into LinesPerPage equal steps. This does
        // not depend on the font size chosen below -- only the horizontal
        // fit does.
        double leading = (double)(PageHeightPoints - 2 * MarginPoints) / LinesPerPage;
        double firstBaseline = PageHeightPoints - MarginPoints - leading;
        double fontSize = FitFontSize(pages);

        for (int index = 0; index < pageCount; index++)
        {
            int pageObject = FirstPageObject + index;
            int contentObject = firstContentObject + index;

            offsets[pageObject] = buffer.Count;
            Emit($"{pageObject} 0 obj\n<< /Type /Page /Parent {PagesObject} 0 R " +
                 $"/MediaBox [0 0 {PageWidthPoints} {PageHeightPoints}] " +
                 $"/Resources << /Font << /F1 {FontObject} 0 R >> >> " +
                 $"/Contents {contentObject} 0 R >>\nendobj\n");

            byte[] content = BuildContentStream(pages[index], leading, firstBaseline, fontSize);

            offsets[contentObject] = buffer.Count;
            Emit($"{contentObject} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            EmitBytes(content);
            Emit("\nendstream\nendobj\n");
        }

        long xrefOffset = buffer.Count;
        Emit($"xref\n0 {totalObjects + 1}\n");
        Emit("0000000000 65535 f \n");
        for (int obj = 1; obj <= totalObjects; obj++)
        {
            Emit($"{offsets[obj]:0000000000} 00000 n \n");
        }

        Emit($"trailer\n<< /Size {totalObjects + 1} /Root {CatalogObject} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return buffer.ToArray();
    }

    /// <summary>
    /// Picks one Courier point size for the whole document: 10 points,
    /// unless the widest line anywhere in the job would not fit the
    /// 523-point text width at that size, in which case the size shrinks
    /// just enough to fit it -- Courier's advance is a fixed 0.6 em per
    /// character, so the fitting size is a single division -- but never
    /// below <see cref="MinimumFontSizePoints"/>, past which a very wide
    /// line simply runs off the page rather than becoming illegible. The
    /// leading, and so the 66 lines per page, is unaffected either way; see
    /// <see cref="BuildPdf"/>.
    /// </summary>
    private static double FitFontSize(List<char>?[][] pages)
    {
        int maxLineLength = 0;
        foreach (List<char>?[] page in pages)
        {
            foreach (List<char>? line in page)
            {
                if (line is not null && line.Count > maxLineLength)
                {
                    maxLineLength = line.Count;
                }
            }
        }

        if (maxLineLength == 0)
        {
            return DefaultFontSizePoints;
        }

        double textWidth = PageWidthPoints - 2 * MarginPoints;
        double widthAtDefaultSize = maxLineLength * CourierAdvanceRatio * DefaultFontSizePoints;
        if (widthAtDefaultSize <= textWidth)
        {
            return DefaultFontSizePoints;
        }

        double fittedSize = textWidth / (maxLineLength * CourierAdvanceRatio);
        return Math.Max(fittedSize, MinimumFontSizePoints);
    }

    /// <summary>
    /// Builds one page's content stream: a single text object that sets the
    /// font, sets the leading, positions the first line's baseline, and
    /// then writes all <see cref="LinesPerPage"/> lines with <c>T*</c>
    /// between them so each one drops down by exactly one leading.
    /// </summary>
    private static byte[] BuildContentStream(List<char>?[] rows, double leading, double firstBaseline, double fontSize)
    {
        var text = new StringBuilder();
        text.Append("BT\n");
        text.Append($"/F1 {FormatNumber(fontSize)} Tf\n");
        text.Append(FormatNumber(leading)).Append(" TL\n");
        text.Append(MarginPoints).Append(' ').Append(FormatNumber(firstBaseline)).Append(" Td\n");

        for (int line = 0; line < rows.Length; line++)
        {
            if (line > 0)
            {
                text.Append("T*\n");
            }
            string lineText = rows[line] is { } characters ? new string(characters.ToArray()) : string.Empty;
            text.Append('(').Append(EscapePdfString(lineText)).Append(")Tj\n");
        }

        text.Append("ET");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Backslash-escapes the three characters a PDF literal string treats specially.</summary>
    private static string EscapePdfString(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (character is '(' or ')' or '\\')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }
        return escaped.ToString();
    }
}
