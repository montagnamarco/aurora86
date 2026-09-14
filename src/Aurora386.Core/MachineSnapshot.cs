using System.Buffers.Binary;
using Aurora386.Core.Configuration;
using Aurora386.Core.Cpu;
using Aurora386.Core.Devices.Pci;
using Aurora386.Core.Devices.Video;

namespace Aurora386.Core;

/// <summary>
/// A copy of the machine's state, taken at an instant and put back later.
///
/// This exists for the sake of investigation rather than for the user. Reaching
/// an interesting moment in a guest can take twenty minutes of emulated boot
/// and installation; a defect that needs twenty attempts to corner is a day's
/// work at that rate and a few minutes with a snapshot taken just before it.
///
/// Captures CPU and RAM together with video, interrupt, timer, keyboard,
/// PCI, USB, DOS integration, and serial mouse state. Device coverage is
/// still incomplete: in-flight IDE transfers are not captured. Disk
/// contents are external state and must be paired with the snapshot by the
/// caller; restoring RAM alone does not undo guest writes to an image.
/// </summary>
public sealed class MachineSnapshot
{
    private const uint VideoTopologyMagic = 0x31444956;
    private const ushort VideoTopologyVersion = 1;
    private const int VideoTopologyHeaderLength = 12;
    private const uint DeviceStateMagic = 0x31535644; // DVS1, after the VID1 payload.
    private const ushort DeviceStateVersion = 1;
    private const int DeviceStateHeaderLength = 8;

    // Absence must survive a load/save cycle. New captures omit the trailer
    // until a device serializer can actually supply continuation state.
    private readonly bool _hasDeviceStateTrailer;

    private readonly byte[] _ram;
    private readonly uint[] _gpr;
    private readonly SegmentCache[] _segments;
    private readonly LazyFlags _flags;

    private readonly uint _eip, _cr0, _cr2, _cr3;
    private readonly uint _gdtBase, _idtBase, _ldtBase, _ldtLimit, _tssBase, _tssLimit;
    private readonly ushort _gdtLimit, _idtLimit, _ldtr, _taskRegister;
    private readonly Flags _otherFlags;
    private readonly byte _cpl;
    private readonly bool _halted, _virtual8086;

    /// <summary>
    /// The A20 gate. It belongs in a snapshot even though it is a chipset
    /// latch rather than processor state: with it closed every address above
    /// the first megabyte silently wraps, so a snapshot restored with the
    /// wrong A20 reads different memory than the one it captured -- the page
    /// tables most of all, and then nothing resolves.
    /// </summary>
    private readonly bool _a20;

    /// <summary>
    /// The video card: its memory and every register that decides what is on
    /// screen.
    ///
    /// Without these a restored machine has the guest's pixels and the BIOS's
    /// idea of the mode, which agree about nothing -- and the guest, having
    /// no reason to redraw a picture it believes is already there, never puts
    /// it right. It is the difference between a snapshot that resumes and one
    /// that resumes blind.
    /// </summary>
    private readonly byte[] _videoMemory;
    private readonly byte[] _videoRegisters;
    private readonly VmVideoModel? _videoModel;
    private readonly byte[] _videoEngine;

    /// <summary>
    /// The chips that decide whether the guest is ever interrupted again.
    ///
    /// A snapshot without them restores the interrupt controllers to their
    /// power-on state, which is every line masked, and the timer to its
    /// power-on divisor. The guest then sits in whatever idle loop it was in,
    /// waiting for a tick that cannot arrive -- and the symptom, a picture
    /// half drawn and a machine that ignores the keyboard, looks exactly like
    /// a fault in whatever the guest was drawing at the time. This cost a
    /// good deal of time to find, so it is worth the forty bytes.
    /// </summary>
    private readonly byte[] _interruptControllers;
    private readonly byte[] _timer;
    private readonly byte[] _keyboard;

    /// <summary>The redirector's attachment to DOS; see <see cref="Machine.CaptureDosState"/>.</summary>
    private readonly byte[] _dosState;
    private readonly byte[] _pci;
    private readonly byte[] _usb;
    private readonly bool? _guestOwnsCompactDisc;
    private readonly bool _hasIdeTimingRegisters;
    private readonly byte[]? _serialMouse;

    private MachineSnapshot(Machine machine)
    {
        CpuState state = machine.Cpu.State;

        _ram = machine.Memory.CaptureRam();
        _gpr = (uint[])state.Gpr.Clone();
        _segments = (SegmentCache[])state.Segments.Clone();
        _flags = machine.Cpu.Flags;

        _eip = state.Eip;
        _cr0 = state.Cr0; _cr2 = state.Cr2; _cr3 = state.Cr3;
        _gdtBase = state.GdtBase; _gdtLimit = state.GdtLimit;
        _idtBase = state.IdtBase; _idtLimit = state.IdtLimit;
        _ldtr = state.Ldtr; _ldtBase = state.LdtBase; _ldtLimit = state.LdtLimit;
        _taskRegister = state.TaskRegister; _tssBase = state.TssBase; _tssLimit = state.TssLimit;
        _otherFlags = state.OtherFlags;
        _cpl = state.CplRaw;
        _halted = state.Halted;
        _virtual8086 = state.Virtual8086Mode;
        _a20 = machine.Memory.A20Enabled;

        _videoMemory = machine.VideoMemory.CaptureMemory();
        _videoRegisters = machine.VideoRegisters.CaptureRegisters();
        _videoModel = machine.VideoModel;
        _videoEngine = machine.Virge?.CaptureEngines() ?? [];

        _interruptControllers = machine.Interrupts.Capture();
        _timer = machine.Timer.Capture();
        _keyboard = machine.Keyboard.Capture();
        _dosState = machine.CaptureDosState();
        _pci = machine.Pci.Capture();
        _usb = machine.CaptureUsbState();
        _guestOwnsCompactDisc = machine.GuestHasItsOwnCompactDiscExtensions;
        _hasIdeTimingRegisters = true;
        _serialMouse = machine.SerialMouse.Capture();
    }

    /// <summary>Takes a snapshot of the machine as it stands.</summary>
    public static MachineSnapshot Capture(Machine machine) => new(machine);

    /// <summary>Puts this state back into a machine.</summary>
    public void RestoreTo(Machine machine)
    {
        PreflightFor(machine);
        CpuState state = machine.Cpu.State;
        IDisposable? videoRestoreScope = machine.Virge?.SuspendSnapshotRestore();

        try
        {
            // Remove the host CD mount before restoring RAM: unmounting clears
            // DOS drive flags, which may already belong to the guest's driver.
            if (_guestOwnsCompactDisc is bool guestOwnsCompactDisc)
            {
                machine.RestoreCompactDiscOwnership(guestOwnsCompactDisc);
            }

            machine.Memory.A20Enabled = _a20;
            machine.Memory.RestoreRam(_ram);
            _gpr.CopyTo(state.Gpr, 0);
            _segments.CopyTo(state.Segments, 0);
            machine.Cpu.Flags = _flags;

            state.Eip = _eip;
            state.Cr0 = _cr0; state.Cr2 = _cr2; state.Cr3 = _cr3;
            state.GdtBase = _gdtBase; state.GdtLimit = _gdtLimit;
            state.IdtBase = _idtBase; state.IdtLimit = _idtLimit;
            state.Ldtr = _ldtr; state.LdtBase = _ldtBase; state.LdtLimit = _ldtLimit;
            state.TaskRegister = _taskRegister; state.TssBase = _tssBase; state.TssLimit = _tssLimit;
            state.OtherFlags = _otherFlags;
            state.CplRaw = _cpl;
            state.Halted = _halted;
            state.Virtual8086Mode = _virtual8086;

            if (_videoMemory.Length > 0)
            {
                machine.VideoMemory.RestoreMemory(_videoMemory);
            }

            if (_videoEngine.Length > 0)
            {
                machine.Virge!.RestoreEngines(_videoEngine);
            }

            if (_videoRegisters.Length > 0)
            {
                machine.VideoRegisters.RestoreRegisters(_videoRegisters);
            }

            // The keyboard controller owns A20, so it is restored after the bus:
            // the other order would leave the line as the snapshot's own field
            // said rather than as the controller had it.
            if (_interruptControllers.Length > 0) machine.Interrupts.Restore(_interruptControllers);
            if (_timer.Length > 0) machine.Timer.Restore(_timer);
            if (_keyboard.Length > 0) machine.Keyboard.Restore(_keyboard);
            if (_serialMouse is not null) machine.SerialMouse.Restore(_serialMouse);

            // After the memory, because attaching marks the CDS entries in it.
            machine.RestoreDosState(_dosState);
            machine.Pci.Restore(_pci);
            if (!_hasIdeTimingRegisters)
            {
                // Earlier PCI snapshots stored zero in unimplemented IDETIM
                // registers while both channels actually decoded their ports.
                machine.Pci.WriteConfiguration(0x0009, 0x40, 4, 0x80008000);
            }
        }
        finally
        {
            videoRestoreScope?.Dispose();
        }

        machine.RestoreUsbState(_usb);

        // Paging translations cached from the old contents of memory describe
        // an address space that no longer exists.
        machine.Cpu.FlushTlb();
    }

    /// <summary>
    /// Validates every snapshot block whose restore can now participate in
    /// video-card reconciliation before changing CD ownership, RAM, or devices.
    /// </summary>
    private void PreflightFor(Machine machine)
    {
        VmVideoModel snapshotModel = _videoModel ?? VmVideoModel.S3Trio64VPlus;
        if (snapshotModel != machine.VideoModel)
        {
            throw new InvalidDataException("Snapshot video model differs from the machine.");
        }

        if (_ram.Length != machine.Memory.RamSize)
        {
            throw new InvalidDataException("Snapshot RAM size differs from the machine.");
        }

        bool virge = snapshotModel == VmVideoModel.S3Virge325;
        ValidateVideoBlockLength(_videoMemory.Length, VgaMemory.TotalSize, virge, "memory");
        ValidateVideoBlockLength(
            _videoRegisters.Length,
            VgaRegisters.SnapshotLength,
            virge,
            "register");
        if (_videoRegisters.Length > 0)
        {
            VgaRegisters.ValidateSnapshot(_videoRegisters);
        }

        if (virge)
        {
            ValidateVirgeEngineSnapshot(_videoEngine);
        }
        else if (_videoEngine.Length != 0)
        {
            throw new InvalidDataException("Trio snapshot contains ViRGE engine state.");
        }

        PciBus.SnapshotState pciSnapshot = machine.Pci.ValidateSnapshot(_pci);
        if (!virge)
        {
            return;
        }

        if (pciSnapshot.IsLegacyReset)
        {
            throw new InvalidDataException("ViRGE snapshot requires complete PCI state.");
        }

        ReadOnlySpan<byte> videoFunction = pciSnapshot.FunctionState(0x0020);
        if (videoFunction.Length != 256
            || BinaryPrimitives.ReadUInt16LittleEndian(videoFunction) != 0x5333
            || BinaryPrimitives.ReadUInt16LittleEndian(videoFunction[2..]) != 0x5631)
        {
            throw new InvalidDataException("ViRGE PCI identity is missing or invalid.");
        }

        uint pciBase = BinaryPrimitives.ReadUInt32LittleEndian(videoFunction[0x10..]) & 0xFC000000;
        uint crtcBase = (uint)(_videoRegisters[0x59] & 0xFC) << 24;
        if (pciBase != crtcBase)
        {
            throw new InvalidDataException("ViRGE PCI BAR and CRTC base latches differ.");
        }
    }

    private static void ValidateVideoBlockLength(
        int actualLength,
        int expectedLength,
        bool required,
        string blockName)
    {
        if (actualLength == expectedLength || (!required && actualLength == 0))
        {
            return;
        }

        throw new InvalidDataException($"Invalid video {blockName} snapshot length.");
    }

    private static void ValidateVirgeEngineSnapshot(ReadOnlySpan<byte> snapshot)
    {
        try
        {
            S3VirgeCard.ValidateEngineSnapshot(snapshot);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid ViRGE engine snapshot.", error);
        }
    }

    /// <summary>Writes a snapshot to a file.</summary>
    public void Save(string path)
    {
        using var file = new BinaryWriter(File.Create(path));

        file.Write(_ram.Length);
        file.Write(_ram);

        foreach (uint value in _gpr) file.Write(value);

        foreach (SegmentCache segment in _segments)
        {
            file.Write(segment.Selector); file.Write(segment.Base); file.Write(segment.Limit);
            file.Write(segment.Big); file.Write(segment.Readable);
            file.Write(segment.Writable); file.Write(segment.ExpandDown); file.Write(segment.Dpl);
        }

        file.Write((byte)_flags.Op); file.Write((uint)_flags.Stored);
        file.Write(_flags.Left); file.Write(_flags.Right);
        file.Write(_flags.Result); file.Write(_flags.Size);

        file.Write(_eip); file.Write(_cr0); file.Write(_cr2); file.Write(_cr3);
        file.Write(_gdtBase); file.Write(_gdtLimit);
        file.Write(_idtBase); file.Write(_idtLimit);
        file.Write(_ldtr); file.Write(_ldtBase); file.Write(_ldtLimit);
        file.Write(_taskRegister); file.Write(_tssBase); file.Write(_tssLimit);
        file.Write((uint)_otherFlags); file.Write(_cpl);
        file.Write(_halted); file.Write(_virtual8086); file.Write(_a20);

        // The video card last, so that a file written before it existed still
        // reads: the loader stops when the stream ends and leaves the fields
        // empty rather than failing.
        file.Write(_videoMemory.Length);
        file.Write(_videoMemory);
        file.Write(_videoRegisters.Length);
        file.Write(_videoRegisters);

        file.Write(_interruptControllers.Length);
        file.Write(_interruptControllers);
        file.Write(_timer.Length);
        file.Write(_timer);
        file.Write(_keyboard.Length);
        file.Write(_keyboard);

        file.Write(_dosState.Length);
        file.Write(_dosState);
        file.Write(_pci.Length);
        file.Write(_pci);
        file.Write(_usb.Length);
        file.Write(_usb);
        if (_guestOwnsCompactDisc.HasValue)
        {
            file.Write(_guestOwnsCompactDisc.Value);
            file.Write(_hasIdeTimingRegisters);
        }

        if (_serialMouse is not null)
        {
            file.Write(_serialMouse.Length);
            file.Write(_serialMouse);
        }

        if (_videoModel.HasValue)
        {
            int payloadLength = VideoTopologyHeaderLength + _videoEngine.Length;
            file.Write(payloadLength);
            file.Write(VideoTopologyMagic);
            file.Write(VideoTopologyVersion);
            file.Write((ushort)_videoModel.Value);
            file.Write(_videoEngine.Length);
            file.Write(_videoEngine);
        }

        if (_hasDeviceStateTrailer)
        {
            file.Write(DeviceStateHeaderLength);
            file.Write(DeviceStateMagic);
            file.Write(DeviceStateVersion);
            file.Write((ushort)0); // No device record kinds are implemented yet.
        }
    }

    private MachineSnapshot(BinaryReader file)
    {
        _ram = file.ReadBytes(file.ReadInt32());

        _gpr = new uint[8];
        for (int i = 0; i < 8; i++) _gpr[i] = file.ReadUInt32();

        _segments = new SegmentCache[6];
        for (int i = 0; i < 6; i++)
        {
            _segments[i].Selector = file.ReadUInt16();
            _segments[i].Base = file.ReadUInt32();
            _segments[i].Limit = file.ReadUInt32();
            _segments[i].Big = file.ReadBoolean();
            _segments[i].Readable = file.ReadBoolean();
            _segments[i].Writable = file.ReadBoolean();
            _segments[i].ExpandDown = file.ReadBoolean();
            _segments[i].Dpl = file.ReadByte();
        }

        _flags.Op = (FlagOp)file.ReadByte();
        _flags.Stored = (Flags)file.ReadUInt32();
        _flags.Left = file.ReadUInt32();
        _flags.Right = file.ReadUInt32();
        _flags.Result = file.ReadUInt32();
        _flags.Size = file.ReadByte();

        _eip = file.ReadUInt32(); _cr0 = file.ReadUInt32();
        _cr2 = file.ReadUInt32(); _cr3 = file.ReadUInt32();
        _gdtBase = file.ReadUInt32(); _gdtLimit = file.ReadUInt16();
        _idtBase = file.ReadUInt32(); _idtLimit = file.ReadUInt16();
        _ldtr = file.ReadUInt16(); _ldtBase = file.ReadUInt32(); _ldtLimit = file.ReadUInt32();
        _taskRegister = file.ReadUInt16(); _tssBase = file.ReadUInt32(); _tssLimit = file.ReadUInt32();
        _otherFlags = (Flags)file.ReadUInt32(); _cpl = file.ReadByte();
        _halted = file.ReadBoolean(); _virtual8086 = file.ReadBoolean();
        _a20 = file.ReadBoolean();

        // A file written before the video card was part of a snapshot simply
        // ends here. Reading it is not an error: what comes back is a
        // machine that resumes with whatever the display happens to hold,
        // which is what those files always meant.
        if (file.BaseStream.Position < file.BaseStream.Length)
        {
            _videoMemory = file.ReadBytes(file.ReadInt32());
            _videoRegisters = file.ReadBytes(file.ReadInt32());
        }
        else
        {
            _videoMemory = [];
            _videoRegisters = [];
        }

        // Same again for the device state, which came later still: a file
        // that stops before it restores a machine whose interrupt
        // controllers are as they power up.
        if (file.BaseStream.Position < file.BaseStream.Length)
        {
            _interruptControllers = file.ReadBytes(file.ReadInt32());
            _timer = file.ReadBytes(file.ReadInt32());
            _keyboard = file.ReadBytes(file.ReadInt32());
        }
        else
        {
            _interruptControllers = [];
            _timer = [];
            _keyboard = [];
        }

        // And the redirector's attachment, later again. A file without it
        // restores a guest whose shared folders have to be found afresh,
        // which for a DOS already at its prompt never happens.
        _dosState = file.BaseStream.Position < file.BaseStream.Length
            ? file.ReadBytes(file.ReadInt32())
            : [];
        _pci = file.BaseStream.Position < file.BaseStream.Length
            ? file.ReadBytes(file.ReadInt32())
            : [];
        _usb = file.BaseStream.Position < file.BaseStream.Length
            ? file.ReadBytes(file.ReadInt32())
            : [];
        // Older snapshots did not record ownership. Keep it unknown rather
        // than claiming that their guest had no native CD-ROM driver.
        _guestOwnsCompactDisc = file.BaseStream.Position < file.BaseStream.Length
            ? file.ReadBoolean()
            : null;
        _hasIdeTimingRegisters = file.BaseStream.Position < file.BaseStream.Length
            && file.ReadBoolean();

        if (file.BaseStream.Position == file.BaseStream.Length)
        {
            _serialMouse = null;
            _videoModel = null;
            _videoEngine = [];
            return;
        }

        long remainingLength = file.BaseStream.Length - file.BaseStream.Position;
        if (remainingLength < sizeof(int))
        {
            throw new InvalidDataException("The serial mouse snapshot length is truncated.");
        }

        int serialMouseLength = file.ReadInt32();
        long payloadLength = file.BaseStream.Length - file.BaseStream.Position;
        if (serialMouseLength < 0 || serialMouseLength > payloadLength)
        {
            throw new InvalidDataException("The serial mouse snapshot length is invalid.");
        }

        _serialMouse = file.ReadBytes(serialMouseLength);
        if (_serialMouse.Length != serialMouseLength)
        {
            throw new InvalidDataException("The serial mouse snapshot payload is truncated.");
        }

        Devices.Input.SerialMouse.ValidateSnapshot(_serialMouse);

        if (file.BaseStream.Position == file.BaseStream.Length)
        {
            _videoModel = null;
            _videoEngine = [];
            return;
        }

        long videoTailLength = file.BaseStream.Length - file.BaseStream.Position;
        if (videoTailLength < sizeof(int))
        {
            throw new InvalidDataException("The video topology snapshot length is truncated.");
        }

        int videoPayloadLength = file.ReadInt32();
        long availableVideoPayload = file.BaseStream.Length - file.BaseStream.Position;
        if (videoPayloadLength < VideoTopologyHeaderLength
            || videoPayloadLength > availableVideoPayload)
        {
            throw new InvalidDataException("The video topology snapshot length is invalid.");
        }

        byte[] videoPayload = file.ReadBytes(videoPayloadLength);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(videoPayload);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(videoPayload.AsSpan(4));
        ushort model = BinaryPrimitives.ReadUInt16LittleEndian(videoPayload.AsSpan(6));
        int engineLength = BinaryPrimitives.ReadInt32LittleEndian(videoPayload.AsSpan(8));
        if (magic != VideoTopologyMagic || version != VideoTopologyVersion)
        {
            throw new InvalidDataException("The video topology snapshot header is invalid.");
        }

        if (model > (ushort)VmVideoModel.S3Virge325)
        {
            throw new InvalidDataException("The video topology snapshot model is invalid.");
        }

        if (engineLength < 0 || engineLength != videoPayloadLength - VideoTopologyHeaderLength)
        {
            throw new InvalidDataException("The video engine snapshot length is invalid.");
        }

        _videoModel = (VmVideoModel)model;
        _videoEngine = videoPayload.AsSpan(VideoTopologyHeaderLength, engineLength).ToArray();
        if (_videoModel == VmVideoModel.S3Trio64VPlus)
        {
            if (_videoEngine.Length != 0)
            {
                throw new InvalidDataException("Trio snapshot contains ViRGE engine state.");
            }
        }
        else
        {
            ValidateVirgeEngineSnapshot(_videoEngine);
        }

        _hasDeviceStateTrailer = ReadDeviceStateTrailer(file);
    }

    /// <summary>
    /// Reads the optional device-state extension without treating unsupported
    /// hardware state as successfully restored. The initial version accepts
    /// only an empty record set; device serializers will extend this boundary.
    /// </summary>
    private static bool ReadDeviceStateTrailer(BinaryReader file)
    {
        long remaining = file.BaseStream.Length - file.BaseStream.Position;
        if (remaining == 0)
        {
            return false;
        }

        if (remaining < sizeof(int))
        {
            throw new InvalidDataException("The device state snapshot length is truncated.");
        }

        int payloadLength = file.ReadInt32();
        remaining -= sizeof(int);
        if (payloadLength < DeviceStateHeaderLength || payloadLength != remaining)
        {
            throw new InvalidDataException("The device state snapshot length is invalid.");
        }

        // Read the fixed header before allocating any record payload. Unknown
        // or future records must fail even if their declared length is huge.
        uint magic = file.ReadUInt32();
        ushort version = file.ReadUInt16();
        ushort recordCount = file.ReadUInt16();
        if (magic != DeviceStateMagic || version != DeviceStateVersion)
        {
            throw new InvalidDataException("The device state snapshot header is invalid.");
        }

        if (recordCount != 0)
        {
            throw new InvalidDataException("Device state snapshot records are not supported yet.");
        }

        if (payloadLength != DeviceStateHeaderLength)
        {
            throw new InvalidDataException("The empty device state snapshot contains trailing data.");
        }

        return true;
    }

    /// <summary>Reads a snapshot back from a file.</summary>
    public static MachineSnapshot Load(string path)
    {
        using var file = new BinaryReader(File.OpenRead(path));
        return new MachineSnapshot(file);
    }
}
