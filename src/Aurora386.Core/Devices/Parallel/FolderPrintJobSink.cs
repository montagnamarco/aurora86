namespace Aurora386.Core.Devices.Parallel;

/// <summary>
/// Where a finished print job lands on the host: a folder holding one
/// `.prn` file per job, and a `.pdf` alongside it when
/// <see cref="PlainTextPdf"/> recognises the job as plain text
/// (docs/PARALLEL-PORT.md section 3). A guest that speaks a printer
/// language -- ESC/P, PCL, PostScript -- gets only the `.prn`, since there
/// is nothing here that understands those languages well enough to render
/// them.
/// </summary>
public sealed class FolderPrintJobSink : IPrintJobSink
{
    /// <summary>
    /// How many job numbers this sink will try before giving up on a
    /// collision. One folder can be shared by more than one machine
    /// instance -- each with its own counter starting at zero -- or hold
    /// leftover files from an earlier run, so a name already in use is a
    /// routine event, not a sign of anything wrong; only exhausting the
    /// whole range is.
    /// </summary>
    private const int MaxFileNameAttempts = 1000;

    private readonly string _folder;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// A number appended to the file name, advanced on every attempt --
    /// successful or not -- so that this sink never proposes the same name
    /// twice even after a collision.
    /// </summary>
    private int _nextJobNumber;

    /// <summary>
    /// The most recent I/O failure writing a job, or null after a job that
    /// wrote cleanly. The port must never fault the guest over a host
    /// filesystem problem -- a full disk or a folder someone deleted while
    /// the emulator was running -- so every failure is caught here instead
    /// of thrown.
    /// </summary>
    public Exception? LastError { get; private set; }

    public FolderPrintJobSink(string folder, Func<DateTimeOffset> clock)
    {
        _folder = folder;
        _clock = clock;
    }

    /// <summary>Writes the job's raw bytes, and a PDF rendering when it is plain text.</summary>
    public void JobCompleted(ReadOnlySpan<byte> job)
    {
        try
        {
            Directory.CreateDirectory(_folder);

            string stamp = _clock().ToString("yyyyMMdd-HHmmss");
            string prnPath = WriteJobFile(stamp, ".prn", job.ToArray());

            if (PlainTextPdf.IsPlainText(job))
            {
                // Same stem as the .prn, created the same way: a stale .pdf
                // left by an earlier run whose .prn was deleted is never
                // overwritten; the PDF is then simply not written.
                string stem = Path.GetFileNameWithoutExtension(prnPath);
                byte[] pdf = PlainTextPdf.Render(job);
                TryCreateNew(Path.Combine(_folder, stem + ".pdf"), pdf);
            }

            LastError = null;
        }
        catch (Exception error)
        {
            // Broad on purpose: this is the boundary to the host
            // filesystem, and every failure it can raise -- a missing
            // parent directory, a permission error, a full disk, a path the
            // host rejects -- means the same thing to a guest that has no
            // idea any of this exists: the job is lost, quietly, on this
            // side of the cable.
            LastError = error;
        }
    }

    /// <summary>
    /// Creates `print-&lt;stamp&gt;-&lt;n&gt;&lt;extension&gt;` with
    /// <see cref="FileMode.CreateNew"/>, which fails atomically rather than
    /// overwriting when that exact name is already taken. On a collision --
    /// this sink's own earlier job at the same second, another sink pointed
    /// at the same folder, or a file left over from a previous run -- the
    /// job number is bumped and the name tried again, up to
    /// <see cref="MaxFileNameAttempts"/> times.
    /// </summary>
    private string WriteJobFile(string stamp, string extension, byte[] bytes)
    {
        for (int attempt = 0; attempt < MaxFileNameAttempts; attempt++)
        {
            string path = Path.Combine(_folder, $"print-{stamp}-{_nextJobNumber}{extension}");
            _nextJobNumber++;

            if (TryCreateNew(path, bytes))
            {
                return path;
            }
        }

        throw new IOException(
            $"Could not find an unused name for a print job in '{_folder}' after {MaxFileNameAttempts} attempts.");
    }

    /// <summary>
    /// Creates <paramref name="path"/> and writes <paramref name="bytes"/>
    /// into it. Returns false only when the name was already taken -- the
    /// one failure a new name fixes. A failure while writing (a full disk)
    /// removes the partial file and propagates, rather than being mistaken
    /// for a collision and retried into a folder of empty files.
    /// </summary>
    private static bool TryCreateNew(string path, byte[] bytes)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        try
        {
            using (stream)
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            return true;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }
}
