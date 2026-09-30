using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Aurora86.Core.Devices.Parallel;

namespace Aurora86.Tests;

/// <summary>
/// Rendering a plain-text print job as a hand-built PDF 1.4 file
/// (docs/PARALLEL-PORT.md section 3).
/// </summary>
public partial class PlainTextPdfTests
{
    [GeneratedRegex(@"(\d{10}) 00000 n ")]
    private static partial Regex XrefEntryPattern();

    private static byte[] Job(params byte[] bytes) => bytes;

    [Fact]
    public void IsPlainTextAcceptsCrLfFormFeedTabAndBackspace()
    {
        byte[] job = "hello\r\nworld\x0C\t\x08"u8.ToArray();

        Assert.True(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void IsPlainTextRefusesAnEscapeByte()
    {
        byte[] job = Job((byte)'A', 0x1B, (byte)'B');

        Assert.False(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void IsPlainTextRefusesAnEmptyJob()
    {
        Assert.False(PlainTextPdf.IsPlainText([]));
    }

    /// <summary>
    /// COPY /A and plenty of period editors append 1Ah (Ctrl-Z) as DOS's own
    /// end-of-file marker. It travels down LPT1 with the rest of the file,
    /// but it was never meant to be printed.
    /// </summary>
    [Fact]
    public void IsPlainTextIgnoresATrailingDosEndOfFileMarker()
    {
        byte[] job = [.. "hello"u8.ToArray(), 0x1A];

        Assert.True(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void IsPlainTextRefusesAJobThatIsOnlyTheEndOfFileMarker()
    {
        Assert.False(PlainTextPdf.IsPlainText([0x1A]));
    }

    [Fact]
    public void IsPlainTextStillRefusesAnEndOfFileMarkerThatIsNotTrailing()
    {
        byte[] job = [(byte)'a', 0x1A, (byte)'b'];

        Assert.False(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void RenderDropsATrailingDosEndOfFileMarkerRatherThanDrawingIt()
    {
        byte[] withMarker = [.. "hi"u8.ToArray(), 0x1A];
        byte[] withoutMarker = "hi"u8.ToArray();

        byte[] pdfWithMarker = PlainTextPdf.Render(withMarker);
        byte[] pdfWithoutMarker = PlainTextPdf.Render(withoutMarker);

        // The marker contributes nothing to lay out, so the two renders --
        // one line of text, nothing else -- are byte for byte identical.
        Assert.Equal(pdfWithoutMarker, pdfWithMarker);
    }

    /// <summary>
    /// A job needing more than 2000 pages is refused before any page is laid
    /// out (*choice*, docs/PARALLEL-PORT.md section 3 does not fix a bound,
    /// this is Aurora86's own safety valve). A run of form feeds is cheap
    /// to construct and still counts as one page each, so the test does not
    /// need to allocate anything close to 2000 real pages of text.
    /// </summary>
    [Fact]
    public void IsPlainTextRefusesAJobNeedingMoreThanTwoThousandPages()
    {
        byte[] job = new byte[2001];
        Array.Fill(job, (byte)0x0C);   // 2001 form feeds: 2002 pages

        Assert.False(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void IsPlainTextAcceptsAJobThatFitsExactlyInTwoThousandPages()
    {
        byte[] job = new byte[1999];
        Array.Fill(job, (byte)0x0C);   // 1999 form feeds: 2000 pages

        Assert.True(PlainTextPdf.IsPlainText(job));
    }

    [Fact]
    public void RenderProducesAFileThatStartsWithThePdfHeaderAndEndsWithEof()
    {
        byte[] pdf = PlainTextPdf.Render("hello"u8.ToArray());
        string text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4\n", text);
        Assert.EndsWith("%%EOF", text);
    }

    [Fact]
    public void RenderProducesOnePagePerFormFeedSeparatedPart()
    {
        // Two form feeds split the job into three parts, so three pages.
        byte[] job = "page one\x0Cpage two\x0Cpage three"u8.ToArray();

        byte[] pdf = PlainTextPdf.Render(job);
        string text = Encoding.Latin1.GetString(pdf);

        // "/Type /Page " (with the trailing space) only matches an actual
        // page object; the page tree's own "/Type /Pages " has an 's'
        // where a page object has a space, so the two counts stay separate.
        Assert.Equal(3, Regex.Matches(text, "/Type /Page ").Count);
        Assert.Single(Regex.Matches(text, "/Type /Pages "));
    }

    /// <summary>
    /// DOS's COPY and most programs end a print job with a form feed (0Ch) to
    /// eject the last sheet. That FF must not add a blank page to the PDF.
    /// </summary>
    [Fact]
    public void AFormFeedEndingTheJobDoesNotAddABlankPage()
    {
        byte[] job = System.Text.Encoding.ASCII.GetBytes("first\fsecond\r\n\f");

        string text = System.Text.Encoding.Latin1.GetString(PlainTextPdf.Render(job));

        Assert.Equal(2, Regex.Matches(text, "/Type /Page ").Count);
    }

    [Fact]
    public void EveryXrefOffsetPointsAtItsObjectsHeader()
    {
        byte[] job = "one\x0Ctwo"u8.ToArray();
        byte[] pdf = PlainTextPdf.Render(job);
        string text = Encoding.Latin1.GetString(pdf);

        MatchCollection matches = XrefEntryPattern().Matches(text);
        Assert.True(matches.Count > 0);

        int objectNumber = 1;
        foreach (Match match in matches)
        {
            long offset = long.Parse(match.Groups[1].Value);
            string atOffset = text.Substring((int)offset, $"{objectNumber} 0 obj".Length);
            Assert.Equal($"{objectNumber} 0 obj", atOffset);
            objectNumber++;
        }
    }

    [Fact]
    public void ParenthesesAndBackslashesAreEscapedInPdfStrings()
    {
        byte[] job = "a(b)c\\d"u8.ToArray();
        byte[] pdf = PlainTextPdf.Render(job);
        string text = Encoding.Latin1.GetString(pdf);

        Assert.Contains(@"(a\(b\)c\\d)Tj", text);
    }

    [GeneratedRegex(@"/F1 ([\d.]+) Tf")]
    private static partial Regex FontSizePattern();

    [Fact]
    public void ALineThatFitsAtTenPointsIsRenderedAtTenPoints()
    {
        byte[] job = new byte[40];
        Array.Fill(job, (byte)'x');   // 40 * 0.6 * 10 = 240 pt, well under the 523 pt text width

        string text = Encoding.Latin1.GetString(PlainTextPdf.Render(job));

        Match match = FontSizePattern().Match(text);
        Assert.True(match.Success);
        Assert.Equal(10.0, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), precision: 3);
    }

    /// <summary>
    /// A line 100 characters wide would need 600 pt at 10-point Courier
    /// (100 * 0.6 * 10), more than the 523-point text width, so the whole
    /// document is set smaller: 523 / (100 * 0.6) ≈ 8.717 pt.
    /// </summary>
    [Fact]
    public void AWideLineShrinksTheWholeDocumentsFontToFitTheTextWidth()
    {
        byte[] job = new byte[100];
        Array.Fill(job, (byte)'x');

        string text = Encoding.Latin1.GetString(PlainTextPdf.Render(job));

        Match match = FontSizePattern().Match(text);
        Assert.True(match.Success);
        double fontSize = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.True(fontSize < 10.0);
        Assert.True(fontSize >= 5.0);
        // The chosen size must actually make the line fit.
        Assert.True(100 * 0.6 * fontSize <= 523.0 + 0.5);
    }

    /// <summary>
    /// A line so wide that fitting it would need less than 5 pt is clamped
    /// at the floor instead: 300 characters would need 523 / (300 * 0.6) ≈
    /// 2.9 pt to fit exactly, well under the 5 pt minimum.
    /// </summary>
    [Fact]
    public void AnExtremelyWideLineIsClampedAtTheFivePointFloorRatherThanShrinkingFurther()
    {
        byte[] job = new byte[300];
        Array.Fill(job, (byte)'x');

        string text = Encoding.Latin1.GetString(PlainTextPdf.Render(job));

        Match match = FontSizePattern().Match(text);
        Assert.True(match.Success);
        Assert.Equal(5.0, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), precision: 3);
    }

    [Fact]
    public void Cp437LowerCaseEAcuteIsMappedToItsWinAnsiByte()
    {
        // Byte 82h is CP437's lower-case e-acute; WinAnsiEncoding draws the
        // same letter at byte E9h.
        byte[] job = [0x82];
        byte[] pdf = PlainTextPdf.Render(job);

        Assert.Contains((byte)0xE9, pdf);
    }

    /// <summary>
    /// A job that is nothing but TABs before one character would pad a line to
    /// eight columns per TAB; the layout stops at the widest column that can
    /// be on paper, so the render stays small and quick.
    /// </summary>
    [Fact]
    public void AFloodOfTabsIsLaidOutWithinTheColumnCap()
    {
        byte[] job = new byte[1_000_001];
        Array.Fill(job, (byte)0x09);
        job[^1] = (byte)'x';

        byte[] pdf = PlainTextPdf.Render(job);

        Assert.True(pdf.Length < 64 * 1024, $"the PDF grew to {pdf.Length} bytes");
    }
}
