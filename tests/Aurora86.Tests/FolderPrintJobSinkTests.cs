using Aurora86.Core.Devices.Parallel;

namespace Aurora86.Tests;

/// <summary>
/// Writing a finished print job to the host filesystem: the raw `.prn`
/// always, and a `.pdf` alongside it for a plain-text job
/// (docs/PARALLEL-PORT.md section 3).
/// </summary>
public class FolderPrintJobSinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aurora86-lpt1-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void APlainTextJobWritesBothAPrnAndAPdf()
    {
        var sink = new FolderPrintJobSink(_folder, () => new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero));

        sink.JobCompleted("hello, printer\r\n"u8.ToArray());

        Assert.Null(sink.LastError);
        string[] files = Directory.GetFiles(_folder);
        Assert.Contains(files, path => path.EndsWith(".prn", StringComparison.Ordinal));
        Assert.Contains(files, path => path.EndsWith(".pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePrnFileNameFollowsThePrintDateTimeSequencePattern()
    {
        var sink = new FolderPrintJobSink(_folder, () => new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero));

        sink.JobCompleted("hello"u8.ToArray());

        string prnPath = Assert.Single(Directory.GetFiles(_folder, "*.prn"));
        string fileName = Path.GetFileName(prnPath);
        Assert.Matches(@"^print-20260929-103000-\d+\.prn$", fileName);
    }

    [Fact]
    public void ANonPlainTextJobWritesOnlyThePrn()
    {
        var sink = new FolderPrintJobSink(_folder, () => DateTimeOffset.UtcNow);

        // An ESC byte disqualifies the job from PlainTextPdf.
        sink.JobCompleted([0x1B, (byte)'@']);

        Assert.Null(sink.LastError);
        Assert.Single(Directory.GetFiles(_folder, "*.prn"));
        Assert.Empty(Directory.GetFiles(_folder, "*.pdf"));
    }

    [Fact]
    public void TwoJobsGetTwoDistinctPrnFiles()
    {
        var sink = new FolderPrintJobSink(_folder, () => DateTimeOffset.UtcNow);

        sink.JobCompleted("first"u8.ToArray());
        sink.JobCompleted("second"u8.ToArray());

        Assert.Equal(2, Directory.GetFiles(_folder, "*.prn").Length);
    }

    [Fact]
    public void TwoSinksAtTheSameFixedClockNeverOverwriteEachOther()
    {
        // Two independent sinks pointed at the same folder -- two machine
        // instances sharing an output directory, say -- each start their
        // own job counter at zero, so without collision handling the
        // second sink's first job would silently overwrite the first's.
        var clock = () => new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);
        var first = new FolderPrintJobSink(_folder, clock);
        var second = new FolderPrintJobSink(_folder, clock);

        first.JobCompleted("from the first sink"u8.ToArray());
        second.JobCompleted("from the second sink"u8.ToArray());

        Assert.Null(first.LastError);
        Assert.Null(second.LastError);

        string[] prnFiles = Directory.GetFiles(_folder, "*.prn");
        Assert.Equal(2, prnFiles.Length);

        var contents = prnFiles.Select(path => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path))).ToList();
        Assert.Contains("from the first sink", contents);
        Assert.Contains("from the second sink", contents);
    }

    [Fact]
    public void AnUnwritableFolderSetsLastErrorWithoutThrowing()
    {
        // A plain file where the sink expects to create a directory: on
        // every platform this repository targets, Directory.CreateDirectory
        // refuses to make a directory where a file of that name exists.
        string blockingFile = Path.Combine(Path.GetTempPath(), "aurora86-lpt1-blocking-" + Guid.NewGuid());
        File.WriteAllText(blockingFile, "not a folder");
        try
        {
            var sink = new FolderPrintJobSink(blockingFile, () => DateTimeOffset.UtcNow);

            sink.JobCompleted("hello"u8.ToArray());

            Assert.NotNull(sink.LastError);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    /// <summary>
    /// A .pdf left behind by an earlier run whose .prn was deleted is never
    /// overwritten: the new job keeps its .prn and simply gets no PDF.
    /// </summary>
    [Fact]
    public void AStalePdfWithTheSameNameIsNeverOverwritten()
    {
        Directory.CreateDirectory(_folder);
        string stalePdf = Path.Combine(_folder, "print-20260929-103000-0.pdf");
        File.WriteAllText(stalePdf, "stale");
        var sink = new FolderPrintJobSink(_folder, () => new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero));

        sink.JobCompleted("hello\r\n"u8);

        Assert.Equal("stale", File.ReadAllText(stalePdf));
        Assert.True(File.Exists(Path.Combine(_folder, "print-20260929-103000-0.prn")));
    }
}
