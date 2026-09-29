namespace Aurora386.Core.Devices.Audio;

/// <summary>
/// The single HD Audio codec <see cref="HdaController"/> talks to over its
/// emulated "HD Audio Link" -- always at codec address 0, the only address
/// <see cref="HdaController.StatestsCodecPresentBit"/> ever announces. Real
/// silicon reaches a codec's registers only through 32-bit verbs (a node ID,
/// a verb ID and a payload) carried either through the CORB/RIRB command
/// ring or the immediate command registers; this class is the codec side of
/// that protocol, <see cref="ExecuteVerb"/> its only entry point.
///
/// <b>Node graph (*choice*, following QEMU's <c>hda-output</c> codec, the
/// identity Linux's generic HD Audio parser and Windows' inbox class driver
/// both accept without a vendor-specific driver).</b> Four nodes: the root
/// (NID 0, vendor/revision only), one Audio Function Group (NID 1, the only
/// function group this codec has), one Audio Output converter -- the DAC
/// (NID 2) -- and one Pin Complex (NID 3) wired to it as its sole connection.
/// There is no input side: this codec is output-only, exactly as the
/// contract names it.
///
/// <b>What is modelled and what is not.</b> Every node parameter, widget
/// control and amplifier register a driver actually probes before starting
/// playback is here: identity, capabilities, the pin's default
/// configuration, the DAC's stream/channel binding, its converter format and
/// its stereo output amplifier (gain and mute, independently per channel).
/// Power state is always reported as D0 (<see cref="ExecutePowerStateGet"/>)
/// since nothing here ever actually suspends; Set Power State is therefore
/// accepted and otherwise ignored. EAPD is plain read/write storage with no
/// behaviour behind it -- no driver this project boots gates audio on it.
/// </summary>
public sealed class HdaCodec
{
    // --- identity: the codec address on the link, and this codec's own node IDs ---

    /// <summary>The only codec address <see cref="HdaController"/> ever talks to -- SDIN line 0.</summary>
    public const byte CodecAddress = 0;

    private const byte RootNodeId = 0;
    private const byte AfgNodeId = 1;
    private const byte DacNodeId = 2;
    private const byte PinNodeId = 3;

    /// <summary>
    /// 1AF4:0022 -- the vendor/device pair QEMU's <c>hda-output</c> codec
    /// reports (*choice*: 1AF4h is Red Hat's PCI vendor ID, used here purely
    /// as an identity token; no driver behaviour depends on the exact
    /// bytes). Read through Get Parameter's Vendor ID parameter (00h) at the
    /// root node.
    /// </summary>
    private const uint VendorDeviceId = 0x1AF40022u;

    /// <summary>Revision ID, Get Parameter 02h at the root node (*choice*, arbitrary but fixed).</summary>
    private const uint RevisionIdValue = 0x00100101u;

    // --- Get Parameter (verb F00h) parameter IDs, HDA spec section 7.3.4 ---

    private const byte ParamVendorId = 0x00;
    private const byte ParamRevisionId = 0x02;
    private const byte ParamSubordinateNodeCount = 0x04;
    private const byte ParamFunctionGroupType = 0x05;
    private const byte ParamAudioFunctionGroupCaps = 0x08;
    private const byte ParamAudioWidgetCaps = 0x09;
    private const byte ParamSupportedPcmRates = 0x0A;
    private const byte ParamSupportedStreamFormats = 0x0B;
    private const byte ParamPinCaps = 0x0C;
    private const byte ParamOutputAmpCaps = 0x0D;
    private const byte ParamConnectionListLength = 0x0E;
    private const byte ParamSupportedPowerStates = 0x0F;
    private const byte ParamInputAmpCaps = 0x12;

    // --- 12-bit verbs (bits 19:8 of the combined verb+payload field), 8-bit payload ---

    private const ushort VerbGetParameter = 0xF00;
    private const ushort VerbGetConnectionSelect = 0xF01;
    private const ushort VerbSetConnectionSelect = 0x701;
    private const ushort VerbGetConnectionListEntry = 0xF02;
    private const ushort VerbGetPowerState = 0xF05;
    private const ushort VerbSetPowerState = 0x705;
    private const ushort VerbGetStreamChannel = 0xF06;
    private const ushort VerbSetStreamChannel = 0x706;
    private const ushort VerbGetPinWidgetControl = 0xF07;
    private const ushort VerbSetPinWidgetControl = 0x707;
    private const ushort VerbGetEapd = 0xF0C;
    private const ushort VerbSetEapd = 0x70C;
    private const ushort VerbGetConfigDefault = 0xF1C;
    private const ushort VerbSetConfigDefaultByte0 = 0x71C;
    private const ushort VerbSetConfigDefaultByte1 = 0x71D;
    private const ushort VerbSetConfigDefaultByte2 = 0x71E;
    private const ushort VerbSetConfigDefaultByte3 = 0x71F;
    private const ushort VerbGetSubsystemId = 0xF20;
    private const ushort VerbFunctionReset = 0x7FF;

    // --- 4-bit "wide" verbs (bits 19:16), 16-bit payload (bits 15:0) --------

    private const uint WideVerbSetConverterFormat = 0x2;
    private const uint WideVerbSetAmpGainMute = 0x3;
    private const uint WideVerbGetConverterFormat = 0xA;
    private const uint WideVerbGetAmpGainMute = 0xB;

    // --- widget capabilities (Get Parameter 09h), HDA spec section 7.3.4.6 ---

    private const uint WidgetCapStereoBit = 1u << 0;
    private const uint WidgetCapOutAmpPresentBit = 1u << 2;
    private const uint WidgetCapAmpParamOverrideBit = 1u << 3;
    private const uint WidgetCapFormatOverrideBit = 1u << 4;
    private const uint WidgetCapConnectionListBit = 1u << 8;
    private const int WidgetCapTypeShift = 20;
    private const uint WidgetTypeAudioOutput = 0x0;
    private const uint WidgetTypePinComplex = 0x4;

    /// <summary>DAC NID 2: stereo, an output amplifier present with its own parameters (not the AFG's shared defaults), and its own format instead of the AFG's default.</summary>
    private const uint DacWidgetCapsValue =
        WidgetCapStereoBit | WidgetCapOutAmpPresentBit | WidgetCapAmpParamOverrideBit | WidgetCapFormatOverrideBit
        | (WidgetTypeAudioOutput << WidgetCapTypeShift);

    /// <summary>Pin NID 3: stereo, a one-entry connection list, no amplifier of its own.</summary>
    private const uint PinWidgetCapsValue =
        WidgetCapStereoBit | WidgetCapConnectionListBit | (WidgetTypePinComplex << WidgetCapTypeShift);

    // --- pin capabilities (Get Parameter 0Ch) --------------------------------

    private const uint PinCapsOutputCapableBit = 1u << 4;
    private const uint PinCapsValue = PinCapsOutputCapableBit; // no presence detect

    // --- supported PCM rates / stream formats (Get Parameter 0Ah/0Bh) -------

    private const uint PcmRates16BitBit = 1u << 17;
    private const uint PcmRate44100Bit = 1u << 5;
    private const uint PcmRate48000Bit = 1u << 6;
    private const uint SupportedPcmRatesValue = PcmRates16BitBit | PcmRate44100Bit | PcmRate48000Bit;
    private const uint StreamFormatPcmBit = 1u << 0;
    private const uint SupportedStreamFormatsValue = StreamFormatPcmBit;

    // --- DAC output amplifier capabilities (Get Parameter 0Dh), HDA spec 7.3.4.10 ---

    private const uint AmpCapsMuteCapableBit = 1u << 31;
    private const int AmpCapsStepSizeShift = 16; // bits 22:16
    private const int AmpCapsNumStepsShift = 8; // bits 14:8
    private const int AmpCapsOffsetShift = 0; // bits 6:0

    /// <summary>
    /// The step size register value: with the HDA spec's own encoding (step
    /// size N means (N+1) x 0.25 dB), 03h is 1.0 dB per step -- see <see
    /// cref="AmpStepDecibels"/>.
    /// </summary>
    private const uint AmpStepSizeRegisterValue = 0x03u;

    /// <summary>75 steps (NumSteps field 4Ah = 74, meaning steps 0..74), offset (the 0 dB point) at step 4Ah -- the contract's own intended value, 80034A4Ah.</summary>
    private const uint DacOutputAmpCapsValue =
        AmpCapsMuteCapableBit
        | (AmpStepSizeRegisterValue << AmpCapsStepSizeShift)
        | (0x4Au << AmpCapsNumStepsShift)
        | (0x4Au << AmpCapsOffsetShift);

    /// <summary>The amplifier's own 0 dB offset step (<see cref="DacOutputAmpCapsValue"/>'s own Offset field, bits 6:0): <see cref="ExecuteSetAmpGainMute"/> and <see cref="ExecuteGetAmpGainMute"/> attenuate below it, boost above it.</summary>
    private const int MaxAmpGainSteps = 0x4A;

    /// <summary>(N+1) x 0.25 dB per step, the HDA spec's own Amplifier Capabilities step-size encoding: with <see cref="AmpStepSizeRegisterValue"/> = 03h, that is 1.0 dB per step.</summary>
    private const double AmpStepDecibels = (AmpStepSizeRegisterValue + 1) * 0.25;

    // --- Set/Get Amplifier Gain/Mute payload bits, HDA spec 7.3.3.7 --------

    private const int SetAmpOutputBit = 1 << 15;
    private const int SetAmpInputBit = 1 << 14;
    private const int SetAmpLeftBit = 1 << 13;
    private const int SetAmpRightBit = 1 << 12;
    private const int SetAmpIndexShift = 8;
    private const int SetAmpIndexMask = 0xF; // bits 11:8
    private const int SetAmpMuteBit = 1 << 7;
    private const int SetAmpGainMask = 0x7F;

    private const int GetAmpOutputBit = 1 << 15;
    private const int GetAmpLeftBit = 1 << 13;
    private const int GetAmpIndexMask = 0xF; // bits 3:0

    // --- Set/Get Converter Format payload: identical bit layout to the controller's own SDnFMT register ---

    // --- pin default configuration (Get/Set Configuration Default) ----------

    /// <summary>1C014010h: *choice* -- jack present, rear panel, line out, green, association 1 sequence 0 (HDA spec section 7.3.3.31's own field layout).</summary>
    private const uint PinConfigDefaultReset = 0x01014010u;

    // --- power states (Get Parameter 0Fh): D0 and D3 only --------------------

    private const uint SupportedPowerStatesValue = 0x9; // bit0 D0, bit3 D3

    private ushort _pinConnectionSelect;
    private byte _pinWidgetControl;
    private byte _pinEapd;
    private uint _pinConfigDefault;

    private byte _dacStreamTagChannel;
    private ushort _dacConverterFormat;
    private int _dacLeftGain;
    private bool _dacLeftMute;
    private int _dacRightGain;
    private bool _dacRightMute;

    public HdaCodec()
    {
        Reset();
    }

    /// <summary>The DAC's own stream tag (bits 7:4 of the last Set Stream/Channel verb, 706h) -- which output stream descriptor's rendered audio <see cref="HdaController"/> mixes to the host. Tag 0 means "unassigned": no driver ever starts playback without first programming a non-zero tag.</summary>
    public int DacStreamTag => _dacStreamTagChannel >> 4;

    /// <summary>The DAC output amplifier's linear gain and mute state for one channel, for <see cref="HdaController"/>'s own host mix.</summary>
    public (double Linear, bool Mute) DacLeftAmp => (GainStepsToLinear(_dacLeftGain), _dacLeftMute);

    /// <summary>See <see cref="DacLeftAmp"/>.</summary>
    public (double Linear, bool Mute) DacRightAmp => (GainStepsToLinear(_dacRightGain), _dacRightMute);

    /// <summary>
    /// Puts every node register back to its power-on default: pin
    /// unmuted-but-disabled, no stream bound, and the DAC amplifier
    /// unmuted at its own 0 dB offset (<see cref="MaxAmpGainSteps"/>) --
    /// *choice*: an audible amplifier at reset, matching QEMU's own
    /// <c>hda-output</c> codec, which likewise powers up unmuted rather
    /// than at minimum gain.
    /// </summary>
    public void Reset()
    {
        _pinConnectionSelect = 0;
        _pinWidgetControl = 0;
        _pinEapd = 0;
        _pinConfigDefault = PinConfigDefaultReset;

        _dacStreamTagChannel = 0;
        _dacConverterFormat = 0;
        _dacLeftGain = MaxAmpGainSteps;
        _dacLeftMute = false;
        _dacRightGain = MaxAmpGainSteps;
        _dacRightMute = false;
    }

    /// <summary>
    /// Runs one 32-bit codec verb (a node ID plus the combined 20-bit
    /// verb-and-payload field <see cref="HdaController"/> already isolated
    /// from the codec address) and returns its 32-bit response. An unknown
    /// verb, or one sent to a node that does not implement it, answers 0 --
    /// exactly what a real codec's own reserved-verb behaviour and this
    /// contract's own "unknown verbs respond 0" both call for.
    /// </summary>
    public uint ExecuteVerb(byte nid, uint verbAndPayload)
    {
        uint wideVerbId = (verbAndPayload >> 16) & 0xF;
        if (wideVerbId is WideVerbSetConverterFormat or WideVerbSetAmpGainMute
            or WideVerbGetConverterFormat or WideVerbGetAmpGainMute)
        {
            return ExecuteWideVerb(nid, wideVerbId, (ushort)(verbAndPayload & 0xFFFF));
        }

        ushort verbId = (ushort)((verbAndPayload >> 8) & 0xFFF);
        byte payload = (byte)(verbAndPayload & 0xFF);
        return ExecuteNarrowVerb(nid, verbId, payload);
    }

    // --- wide (4-bit ID, 16-bit payload) verbs: converter format and amplifier gain/mute ---

    private uint ExecuteWideVerb(byte nid, uint wideVerbId, ushort payload)
    {
        if (nid != DacNodeId)
        {
            return 0; // only the DAC has a converter format or an amplifier of its own
        }

        return wideVerbId switch
        {
            WideVerbSetConverterFormat => ExecuteSetConverterFormat(payload),
            WideVerbGetConverterFormat => _dacConverterFormat,
            WideVerbSetAmpGainMute => ExecuteSetAmpGainMute(payload),
            WideVerbGetAmpGainMute => ExecuteGetAmpGainMute(payload),
            _ => 0,
        };
    }

    private uint ExecuteSetConverterFormat(ushort payload)
    {
        // The controller's own SDnFMT register is what actually paces DMA
        // (see HdaController.Streams.cs); this register exists only so a
        // driver's read-back of what it programmed on the codec side sees
        // its own value, matching a real two-chip link where the codec and
        // the controller each keep their own copy of the format.
        _dacConverterFormat = payload;
        return 0;
    }

    /// <summary>
    /// HDA spec 7.3.3.7: bit 15 selects the output amp, bit 14 the input
    /// amp, bit 13/12 left/right, bits 11:8 the amplifier index, bit 7
    /// mute, bits 6:0 gain. This codec has exactly one amplifier -- the
    /// DAC's own output amp at index 0 -- so a Set naming the input amp,
    /// or any index but 0, changes nothing.
    /// </summary>
    private uint ExecuteSetAmpGainMute(ushort payload)
    {
        bool outputAmpSelected = (payload & SetAmpOutputBit) != 0;
        int index = (payload >> SetAmpIndexShift) & SetAmpIndexMask;
        if (!outputAmpSelected || index != 0)
        {
            return 0;
        }

        int gain = payload & SetAmpGainMask;
        bool mute = (payload & SetAmpMuteBit) != 0;

        if ((payload & SetAmpLeftBit) != 0)
        {
            _dacLeftGain = gain;
            _dacLeftMute = mute;
        }

        if ((payload & SetAmpRightBit) != 0)
        {
            _dacRightGain = gain;
            _dacRightMute = mute;
        }

        return 0;
    }

    /// <summary>
    /// HDA spec 7.3.3.7: bit 15 selects the output amp (there is no input
    /// amp to select with bit 14 here, since Get only ever names one
    /// side), bit 13 left versus right, bits 3:0 the amplifier index. The
    /// response is bit 7 mute, bits 6:0 gain. A Get for the nonexistent
    /// amp (output bit clear) or any index but 0 answers 0.
    /// </summary>
    private uint ExecuteGetAmpGainMute(ushort payload)
    {
        bool outputAmpSelected = (payload & GetAmpOutputBit) != 0;
        int index = payload & GetAmpIndexMask;
        if (!outputAmpSelected || index != 0)
        {
            return 0;
        }

        bool left = (payload & GetAmpLeftBit) != 0;
        int gain = left ? _dacLeftGain : _dacRightGain;
        bool mute = left ? _dacLeftMute : _dacRightMute;
        uint response = (uint)(gain & SetAmpGainMask);
        if (mute)
        {
            response |= SetAmpMuteBit;
        }

        return response;
    }

    // --- narrow (12-bit ID, 8-bit payload) verbs -----------------------------

    private uint ExecuteNarrowVerb(byte nid, ushort verbId, byte payload) => verbId switch
    {
        VerbGetParameter => GetParameter(nid, payload),
        VerbGetConnectionSelect => nid == PinNodeId ? _pinConnectionSelect : (uint)0,
        VerbSetConnectionSelect => ExecuteSetConnectionSelect(nid, payload),
        VerbGetConnectionListEntry => nid == PinNodeId ? GetConnectionListEntry(payload) : 0u,
        VerbGetPowerState => 0u, // always D0 set and actual, per the contract
        VerbSetPowerState => 0u, // accepted, nothing to change: this codec never suspends
        VerbGetStreamChannel => nid == DacNodeId ? _dacStreamTagChannel : (uint)0,
        VerbSetStreamChannel => ExecuteSetStreamChannel(nid, payload),
        VerbGetPinWidgetControl => nid == PinNodeId ? _pinWidgetControl : (uint)0,
        VerbSetPinWidgetControl => ExecuteSetPinWidgetControl(nid, payload),
        VerbGetEapd => nid == PinNodeId ? _pinEapd : (uint)0,
        VerbSetEapd => ExecuteSetEapd(nid, payload),
        VerbGetConfigDefault => nid == PinNodeId ? _pinConfigDefault : 0u,
        VerbSetConfigDefaultByte0 => ExecuteSetConfigDefaultByte(nid, 0, payload),
        VerbSetConfigDefaultByte1 => ExecuteSetConfigDefaultByte(nid, 1, payload),
        VerbSetConfigDefaultByte2 => ExecuteSetConfigDefaultByte(nid, 2, payload),
        VerbSetConfigDefaultByte3 => ExecuteSetConfigDefaultByte(nid, 3, payload),
        VerbGetSubsystemId => 0u, // *choice*: not programmed, and no driver behaviour this project boots depends on it
        VerbFunctionReset => ExecuteFunctionReset(nid),
        _ => 0u,
    };

    private uint GetParameter(byte nid, byte parameterId) => parameterId switch
    {
        ParamVendorId => nid == RootNodeId ? VendorDeviceId : 0u,
        ParamRevisionId => nid == RootNodeId ? RevisionIdValue : 0u,
        ParamSubordinateNodeCount => GetSubordinateNodeCount(nid),
        ParamFunctionGroupType => nid == AfgNodeId ? 0x01u : 0u, // 01h: audio function group
        ParamAudioFunctionGroupCaps => 0u,
        ParamAudioWidgetCaps => GetAudioWidgetCaps(nid),
        ParamSupportedPcmRates => nid is AfgNodeId or DacNodeId or PinNodeId ? SupportedPcmRatesValue : 0u,
        ParamSupportedStreamFormats => nid is AfgNodeId or DacNodeId or PinNodeId ? SupportedStreamFormatsValue : 0u,
        ParamPinCaps => nid == PinNodeId ? PinCapsValue : 0u,
        ParamOutputAmpCaps => nid == DacNodeId ? DacOutputAmpCapsValue : 0u,
        ParamInputAmpCaps => 0u, // this codec has no input side
        ParamConnectionListLength => nid == PinNodeId ? 1u : 0u,
        ParamSupportedPowerStates => nid == AfgNodeId ? SupportedPowerStatesValue : 0u,
        _ => 0u,
    };

    /// <summary>Root: one AFG (NID 1). AFG: two widgets, the DAC and the pin (NIDs 2-3). Response format (HDA spec 7.3.4.3): bits 23:16 starting node number, bits 7:0 total number of nodes.</summary>
    private static uint GetSubordinateNodeCount(byte nid) => nid switch
    {
        RootNodeId => ((uint)AfgNodeId << 16) | 1u,
        AfgNodeId => ((uint)DacNodeId << 16) | 2u,
        _ => 0u,
    };

    private static uint GetAudioWidgetCaps(byte nid) => nid switch
    {
        DacNodeId => DacWidgetCapsValue,
        PinNodeId => PinWidgetCapsValue,
        _ => 0u,
    };

    /// <summary>The pin's own one-entry connection list: its sole entry is the DAC, NID 2, in the response's low byte -- the contract's own "F02 returns 02h".</summary>
    private static uint GetConnectionListEntry(byte entryGroupIndex) => entryGroupIndex == 0 ? DacNodeId : 0u;

    private uint ExecuteSetConnectionSelect(byte nid, byte payload)
    {
        if (nid == PinNodeId)
        {
            // Only entry 0 (the DAC) exists; storing whatever the driver
            // wrote lets a read-back see it, but the pin's only real input
            // is the DAC regardless of the index programmed here.
            _pinConnectionSelect = payload;
        }

        return 0;
    }

    private uint ExecuteSetStreamChannel(byte nid, byte payload)
    {
        if (nid == DacNodeId)
        {
            _dacStreamTagChannel = payload;
        }

        return 0;
    }

    private uint ExecuteSetPinWidgetControl(byte nid, byte payload)
    {
        if (nid == PinNodeId)
        {
            _pinWidgetControl = payload;
        }

        return 0;
    }

    private uint ExecuteSetEapd(byte nid, byte payload)
    {
        if (nid == PinNodeId)
        {
            _pinEapd = payload;
        }

        return 0;
    }

    private uint ExecuteSetConfigDefaultByte(byte nid, int byteIndex, byte payload)
    {
        if (nid != PinNodeId)
        {
            return 0;
        }

        int shift = byteIndex * 8;
        _pinConfigDefault = (_pinConfigDefault & ~(0xFFu << shift)) | ((uint)payload << shift);
        return 0;
    }

    private uint ExecuteFunctionReset(byte nid)
    {
        if (nid == AfgNodeId)
        {
            Reset();
        }

        return 0;
    }

    /// <summary>Converts an amplifier gain register value (0..74) to a linear multiplier, with the top step (<see cref="MaxAmpGainSteps"/>) as 0 dB.</summary>
    private static double GainStepsToLinear(int gainSteps)
    {
        double decibels = (gainSteps - MaxAmpGainSteps) * AmpStepDecibels;
        return Math.Pow(10.0, decibels / 20.0);
    }

    // --- snapshot: embedded in HdaController.Capture/Restore -----------------

    /// <summary>The fixed byte length <see cref="Capture"/> writes: stream tag/channel, converter format, the four amplifier fields, and the pin's own four registers.</summary>
    public const int PayloadLength =
        sizeof(byte)  // DAC stream tag/channel
        + sizeof(ushort) // DAC converter format
        + sizeof(int) * 2 // DAC left/right gain
        + sizeof(bool) * 2 // DAC left/right mute
        + sizeof(uint) // pin configuration default
        + sizeof(byte) // pin widget control
        + sizeof(ushort) // pin connection select
        + sizeof(byte); // pin EAPD

    /// <summary>Writes every register in the order <see cref="Restore"/> expects. See <see cref="HdaController.Capture"/> for the enclosing payload.</summary>
    public void Capture(BinaryWriter writer)
    {
        writer.Write(_dacStreamTagChannel);
        writer.Write(_dacConverterFormat);
        writer.Write(_dacLeftGain);
        writer.Write(_dacRightGain);
        writer.Write(_dacLeftMute);
        writer.Write(_dacRightMute);
        writer.Write(_pinConfigDefault);
        writer.Write(_pinWidgetControl);
        writer.Write(_pinConnectionSelect);
        writer.Write(_pinEapd);
    }

    /// <summary>Restores what <see cref="Capture"/> wrote. See <see cref="HdaController.Restore"/>.</summary>
    public void Restore(BinaryReader reader)
    {
        _dacStreamTagChannel = reader.ReadByte();
        _dacConverterFormat = reader.ReadUInt16();
        _dacLeftGain = reader.ReadInt32();
        _dacRightGain = reader.ReadInt32();
        _dacLeftMute = reader.ReadBoolean();
        _dacRightMute = reader.ReadBoolean();
        _pinConfigDefault = reader.ReadUInt32();
        _pinWidgetControl = reader.ReadByte();
        _pinConnectionSelect = reader.ReadUInt16();
        _pinEapd = reader.ReadByte();
    }
}
