namespace Aurora86.Core.Devices.Parallel;

/// <summary>
/// Where a completed print job goes once <see cref="PrintJobCollector"/>
/// decides it is finished. Aurora86's own implementation,
/// <see cref="FolderPrintJobSink"/>, writes it to the host filesystem; a
/// test rig substitutes something that just remembers what it was handed.
/// </summary>
public interface IPrintJobSink
{
    /// <summary>The exact bytes of one finished job, in the order the guest sent them.</summary>
    void JobCompleted(ReadOnlySpan<byte> job);
}
