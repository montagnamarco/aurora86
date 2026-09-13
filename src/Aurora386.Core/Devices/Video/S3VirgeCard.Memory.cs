using Aurora386.Core.Memory;

namespace Aurora386.Core.Devices.Video;

public sealed partial class S3VirgeCard
{
    private const uint NewMmioOffset = 0x01000000;
    private const uint NewMmioSize = 0x10000;
    private const uint BigEndianOffset = 0x02000000;
    private const uint ConfigurationStart = 0x8000;
    private const uint ConfigurationEnd = 0x8044;
    private const uint VgaAliasStart = 0x83B0;
    private const uint VgaAliasEnd = 0x83E0;

    byte IMemoryDevice.ReadByte(uint offset) => (byte)ReadMemory(offset, 1);
    ushort IMemoryDevice.ReadWord(uint offset) => (ushort)ReadMemory(offset, 2);
    uint IMemoryDevice.ReadDword(uint offset) => ReadMemory(offset, 4);

    void IMemoryDevice.WriteByte(uint offset, byte value)
    {
        WriteMemory(offset, 1, value);
    }

    void IMemoryDevice.WriteWord(uint offset, ushort value)
    {
        WriteMemory(offset, 2, value);
    }

    void IMemoryDevice.WriteDword(uint offset, uint value)
    {
        WriteMemory(offset, 4, value);
    }

    /// <summary>
    /// Resolves the new little-endian windows from databook table 15-1.
    /// The 64 MB BAR reservation includes unsupported regions; all-ones there
    /// is an explicit emulator limitation, not a silicon measurement.
    /// </summary>
    private uint ReadMemory(uint offset, int width)
    {
        if (_disposed)
        {
            return uint.MaxValue;
        }

        if (IsLinearAccess(offset, width))
        {
            uint value = 0;
            for (int index = 0; index < width; index++)
            {
                value |= (uint)VideoMemory.ReadLinear(offset + (uint)index) << (index * 8);
            }

            return value;
        }

        if (TryGetMmio(offset, width, out uint register))
        {
            if (IsWithin(register, width, ConfigurationStart, ConfigurationEnd))
            {
                if ((register & (uint)(width - 1)) == 0)
                {
                    return Function.Read((int)(register - ConfigurationStart), width);
                }

                ReportUnsupported(offset, width, false, S3VirgeUnsupportedReason.MisalignedConfiguration);
                return uint.MaxValue;
            }

            if (IsWithin(register, width, VgaAliasStart, VgaAliasEnd))
            {
                uint value = 0;
                for (int index = 0; index < width; index++)
                {
                    ushort port = (ushort)((register + (uint)index) & 0x3FF);
                    value |= (uint)Registers.ReadByte(port) << (index * 8);
                }

                return value;
            }

            if (IsEngineAccess(register, width))
            {
                IMemoryDevice engine = Engine;
                return width switch
                {
                    1 => engine.ReadByte(register),
                    2 => engine.ReadWord(register),
                    _ => engine.ReadDword(register)
                };
            }
        }

        ReportUnsupported(offset, width, false, UnsupportedReason(offset));
        return uint.MaxValue;
    }

    /// <summary>
    /// Preserves transaction widths at the drawing engine: splitting a DWORD
    /// into bytes would discard the command instead of triggering the triangle.
    /// </summary>
    private void WriteMemory(uint offset, int width, uint value)
    {
        if (_disposed)
        {
            return;
        }

        if (IsLinearAccess(offset, width))
        {
            for (int index = 0; index < width; index++)
            {
                VideoMemory.WriteLinear(offset + (uint)index, (byte)(value >> (index * 8)));
            }

            return;
        }

        if (TryGetMmio(offset, width, out uint register))
        {
            if (IsWithin(register, width, ConfigurationStart, ConfigurationEnd))
            {
                if ((register & (uint)(width - 1)) == 0)
                {
                    Function.Write((int)(register - ConfigurationStart), width, value);
                    return;
                }

                ReportUnsupported(offset, width, true, S3VirgeUnsupportedReason.MisalignedConfiguration);
                return;
            }

            if (IsWithin(register, width, VgaAliasStart, VgaAliasEnd))
            {
                for (int index = 0; index < width; index++)
                {
                    ushort port = (ushort)((register + (uint)index) & 0x3FF);
                    Registers.WriteByte(port, (byte)(value >> (index * 8)));
                }

                return;
            }

            if (IsEngineAccess(register, width))
            {
                IMemoryDevice engine = Engine;
                switch (width)
                {
                    case 1:
                        engine.WriteByte(register, (byte)value);
                        break;
                    case 2:
                        engine.WriteWord(register, (ushort)value);
                        break;
                    case 4:
                        engine.WriteDword(register, value);
                        break;
                }

                return;
            }
        }

        ReportUnsupported(offset, width, true, UnsupportedReason(offset));
    }

    private bool IsLinearAccess(uint offset, int width)
    {
        S3VirgeApertureControls aperture = Registers.VirgeAperture!;
        // Section 15-3 requires both enhanced mapping bits as well as CR58.
        // Unlike the old window, only address bits 31:26 select the new base.
        uint usableSize = Math.Min(aperture.LinearWindowSize, (uint)VgaMemory.TotalSize);
        return aperture.NewMmioEnabled && aperture.LinearEnabled
            && (Registers.Crtc(0x66) & 1) != 0
            && (Registers.Crtc(0x31) & 8) != 0
            && (ulong)offset + (uint)width <= usableSize;
    }

    private bool TryGetMmio(uint offset, int width, out uint register)
    {
        register = offset - NewMmioOffset;
        return Registers.VirgeAperture!.NewMmioEnabled
            && IsWithin(offset, width, NewMmioOffset, NewMmioOffset + NewMmioSize);
    }

    private static bool IsEngineAccess(uint register, int width)
    {
        return IsWithin(register, width, 0x8504, 0x8508)
            || IsWithin(register, width, 0xB400, 0xB600);
    }

    private static bool IsWithin(uint offset, int width, uint start, uint end)
    {
        return offset >= start && (ulong)offset + (uint)width <= end;
    }

    private static S3VirgeUnsupportedReason UnsupportedReason(uint offset)
    {
        return offset >= BigEndianOffset
            ? S3VirgeUnsupportedReason.BigEndian
            : S3VirgeUnsupportedReason.ReservedOrUnimplemented;
    }
}
