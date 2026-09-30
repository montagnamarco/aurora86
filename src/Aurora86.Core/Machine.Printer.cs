using Aurora86.Core.Devices.Parallel;

namespace Aurora86.Core;

/// <summary>
/// The printer on LPT1 (docs/PARALLEL-PORT.md): where its finished jobs go.
/// The port and the printer are always fitted, so DOS and Windows find LPT1
/// as on any PC; the jobs reach the host only once a front end or a machine
/// folder names an output folder, and are dropped otherwise, as if the
/// printer were switched on with no paper path behind it.
/// </summary>
public sealed partial class Machine
{
    private readonly ForwardingPrintJobSink _printOutput = new();
    private string? _printOutputFolder;

    /// <summary>
    /// The host folder finished print jobs are written to (a .prn with the
    /// bytes as sent, plus a .pdf for plain text), or null to drop them.
    /// </summary>
    public string? PrintOutputFolder
    {
        get => _printOutputFolder;
        set
        {
            _printOutputFolder = value;
            _printOutput.Target = value is null
                ? null
                : new FolderPrintJobSink(value, () => DateTimeOffset.Now);
        }
    }

    /// <summary>The folder sink, when one is set, for its last write error.</summary>
    public FolderPrintJobSink? PrintOutput => _printOutput.Target as FolderPrintJobSink;

    /// <summary>Hands jobs to whichever sink is current; the collector keeps one fixed reference to this.</summary>
    private sealed class ForwardingPrintJobSink : IPrintJobSink
    {
        public IPrintJobSink? Target { get; set; }

        public void JobCompleted(ReadOnlySpan<byte> job)
        {
            Target?.JobCompleted(job);
        }
    }
}
