using System.Buffers.Binary;
using Aurora386.Core;
using Aurora386.Core.Configuration;
using Aurora386.Core.Devices.Usb;

namespace Aurora386.Tests;

public class UsbMassStorageTests
{
    private bool _input, _output;
    private static void Control(UsbMassStorageDevice disk, byte type, byte request, ushort value = 0, ushort index = 0)
    {
        byte[] setup = [type, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), 0, 0];
        Assert.Equal(UsbHandshake.Ack, disk.Transfer(0x2D, 0, false, setup).Handshake);
        Assert.Equal(UsbHandshake.Ack, disk.Transfer(0x69, 0, true, []).Handshake);
    }
    private static UsbMassStorageDevice Disk(MemoryStream image, bool readOnly = false)
    {
        var disk = new UsbMassStorageDevice(image, readOnly); Control(disk, 0, 9, 1); return disk;
    }
    private byte[] Read(UsbMassStorageDevice disk, int capacity = 64)
    {
        byte[] packet = new byte[capacity]; var result = disk.Transfer(0x69, 1, _input, packet);
        Assert.Equal(UsbHandshake.Ack, result.Handshake); _input = !_input;
        return packet[..result.Length];
    }
    private void Write(UsbMassStorageDevice disk, byte[] data)
    {
        Assert.Equal(UsbHandshake.Ack, disk.Transfer(0xE1, 2, _output, data).Handshake); _output = !_output;
    }
    private static byte[] Cbw(byte[] cdb, uint length, bool input)
    {
        byte[] cbw = new byte[31]; BinaryPrimitives.WriteUInt32LittleEndian(cbw, 0x43425355);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8), length);
        cbw[12] = input ? (byte)128 : (byte)0; cbw[14] = (byte)cdb.Length; cdb.CopyTo(cbw, 15); return cbw;
    }
    private void Status(UsbMassStorageDevice disk, byte status = 0, uint residue = 0)
    {
        byte[] csw = Read(disk); Assert.Equal(13, csw.Length);
        Assert.Equal(0x53425355u, BinaryPrimitives.ReadUInt32LittleEndian(csw));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(4)));
        Assert.Equal(residue, BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(8))); Assert.Equal(status, csw[12]);
    }
    private static byte[] Rw(byte op, uint lba, ushort blocks = 1)
    {
        byte[] cdb = new byte[10]; cdb[0] = op;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2), lba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), blocks); return cdb;
    }

    [Fact]
    public void InquiryAndCapacityReturnScsiDataAndCorrectResidue()
    {
        using var disk = Disk(new MemoryStream(new byte[4096]));
        Write(disk, Cbw([0x12,0,0,0,36,0], 64, true));
        byte[] inquiry = Read(disk); Assert.Equal(36, inquiry.Length); Assert.Equal(128, inquiry[1]);
        Assert.Equal("AURORA  ", System.Text.Encoding.ASCII.GetString(inquiry, 8, 8)); Status(disk, residue: 28);
        Write(disk, Cbw(Rw(0x25, 0, 0), 8, true));
        byte[] capacity = Read(disk); Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(capacity));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32BigEndian(capacity.AsSpan(4))); Status(disk);
    }

    [Fact]
    public void WriteCommitsOnlyAfterTheWholeSectorAndReadReturnsIt()
    {
        var image = new MemoryStream(new byte[4096]); using var disk = Disk(image);
        byte[] sector = Enumerable.Range(0,512).Select(i => (byte)(i * 17)).ToArray();
        Write(disk, Cbw(Rw(0x2A, 3), 512, false));
        for (int i = 0; i < 7; i++) Write(disk, sector[(i*64)..((i+1)*64)]);
        Assert.All(image.ToArray(), value => Assert.Equal(0, value));
        Write(disk, sector[448..]); Status(disk);
        Assert.Equal(sector, image.ToArray()[1536..2048]);
        Write(disk, Cbw(Rw(0x28, 3), 512, true));
        var read = new List<byte>(); for (int i = 0; i < 8; i++) read.AddRange(Read(disk));
        Assert.Equal(sector, read); Status(disk);
    }

    [Fact]
    public void ReadOnlyWriteStallsAndReportsDataProtectWithoutChangingImage()
    {
        var image = new MemoryStream(new byte[4096]); using var disk = Disk(image, true);
        Write(disk, Cbw(Rw(0x2A, 0), 512, false));
        Assert.Equal(UsbHandshake.Stall, disk.Transfer(0xE1, 2, _output, new byte[64]).Handshake);
        Control(disk, 2, 1, index: 2); _output = false;
        Status(disk, 1, 512);
        Write(disk, Cbw([3,0,0,0,18,0], 18, true));
        byte[] sense = Read(disk); Assert.Equal(7, sense[2]); Assert.Equal(0x27, sense[12]); Status(disk);
        Assert.All(image.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void OutOfRangeReadReportsIllegalLba()
    {
        using var disk = Disk(new MemoryStream(new byte[4096]));
        Write(disk, Cbw(Rw(0x28, 8), 512, true));
        Assert.Equal(UsbHandshake.Stall, disk.Transfer(0x69, 1, _input, new byte[64]).Handshake);
        Control(disk, 2, 1, index: 0x81); _input = false; Status(disk, 1, 512);
        Write(disk, Cbw([3,0,0,0,18,0], 18, true));
        byte[] sense = Read(disk); Assert.Equal(5, sense[2]); Assert.Equal(0x21, sense[12]); Status(disk);
    }

    [Fact]
    public void InvalidCbwRequiresBulkResetAndEndpointClearHalt()
    {
        using var disk = Disk(new MemoryStream(new byte[4096]));
        Assert.Equal(UsbHandshake.Stall, disk.Transfer(0xE1, 2, false, new byte[31]).Handshake);
        Control(disk, 2, 1, index: 2);
        Assert.Equal(UsbHandshake.Stall, disk.Transfer(0xE1, 2, false, Cbw([0,0,0,0,0,0],0,false)).Handshake);
        Control(disk, 0x21, 0xFF); Control(disk, 2, 1, index: 2); Control(disk, 2, 1, index: 0x81);
        Write(disk, Cbw([0,0,0,0,0,0],0,false)); Status(disk);
    }

    [Fact]
    public void SnapshotRestoresPartialWriteAndDuplicatePacketsDoNotWriteTwice()
    {
        var image = new MemoryStream(new byte[4096]); using var disk = Disk(image);
        Write(disk, Cbw(Rw(0x2A, 2),512,false));
        byte[] packet = Enumerable.Repeat((byte)0xA5,64).ToArray();
        Write(disk,packet); byte[] state = disk.Capture();
        disk.Reset(); disk.Restore(state);
        Assert.Equal(UsbHandshake.Ack, disk.Transfer(0xE1,2,!_output,packet).Handshake);
        for(int i=0;i<6;i++) Write(disk,packet);
        Assert.All(image.ToArray(), value => Assert.Equal(0,value));
        Write(disk,packet); Status(disk);
        Assert.All(image.ToArray()[1024..1536], value => Assert.Equal(0xA5,value));
    }

    [Fact]
    public void ShortHostWriteReturnsPhaseErrorWithoutCommittingPartialSector()
    {
        var image = new MemoryStream(new byte[4096]); using var disk = Disk(image);
        Write(disk,Cbw(Rw(0x2A,0),64,false)); Write(disk,new byte[64]); Status(disk,2,64);
        Assert.All(image.ToArray(), value => Assert.Equal(0,value));
        Assert.Equal(UsbHandshake.Stall,disk.Transfer(0xE1,2,_output,Cbw([0,0,0,0,0,0],0,false)).Handshake);
    }

    [Fact]
    public void ThirdDeviceCoexistsWithHidAndHotplugKeepsPciTopology()
    {
        using var machine = new Machine(); machine.EnableUsb(); machine.RunInstructions(3000);
        Assert.NotNull(machine.UsbKeyboard); Assert.NotNull(machine.UsbMouse);
        Assert.Equal(0, machine.Io.ReadWord(0xC130)&1);
        machine.AttachUsbStorage(new UsbMassStorageDevice(new MemoryStream(new byte[4096])));
        Assert.Equal(3,machine.Io.ReadWord(0xC130)&3);
        Control(machine.UsbStorage!,0,9,1);
        var saved = MachineSnapshot.Capture(machine);
        machine.UsbStorage!.Reset(); saved.RestoreTo(machine);
        Assert.Equal(1,machine.UsbStorage.Configuration);
        machine.EjectUsbStorage(); Assert.Equal(2,machine.Io.ReadWord(0xC130)&3);
        Assert.Equal(0x70208086u,machine.Pci.ReadConfiguration(0x18,0,4));
    }

    [Fact]
    public void VmConfigurationPreservesRelativeImagePathAndReadOnlySetting()
    {
        string directory = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory,"stick.img"); File.WriteAllBytes(path,new byte[4096]);
            var config = new VmConfiguration { UsbEnabled = true, UsbStorage = new() { Path = path, ReadOnly = true } };
            config.Save(directory);
            var loaded = VmConfiguration.Load(directory);
            Assert.Equal(path,loaded.UsbStorage!.Path);
            using var machine = loaded.BuildMachine(); Assert.True(machine.UsbStorage!.ReadOnly);
            Assert.NotNull(machine.UsbKeyboard); Assert.NotNull(machine.UsbMouse);
        }
        finally { Directory.Delete(directory,true); }
    }
}
