# ACPI (work package P4.5)

ACPI 1.0b, on the i430TX/PIIX4 board only (`hardware.chipset` = `"i430tx"`
in `vm.json`, or `new Machine(chipset: Chipset.I430Tx)`). The i430FX board
carries none of this: no RSDP, no ACPI tables in ROM, and pressing the host
power button always calls `Machine.RequestPowerOff()` directly, exactly as
before this package.

## Tables

Built once, in C#, when the BIOS ROM image is assembled
(`Machine.BuildBiosImage`, `BiosRom.WriteAcpiTables`) and placed at ROM
offset `2000h`, right after the MP 1.4 tables' own region (which ends
exactly there) and well inside the free space the BIOS layout keeps between
`2000h` and `FFF0h`. A CPU-model switch (`Machine.CpuModel`'s setter)
rebuilds this whole region the same way it already rebuilds the MP tables,
since the MADT's own existence, the FADT's INT_MODEL byte and the RSDT's
pointer count all depend on the same silicon fact the MP tables do (an
on-die local APIC).

- **RSDP** -- ACPI 1.0b §5.2.5.1, revision 0, 20 bytes, no XSDT.
- **RSDT** -- points at the FADT always, and at the MADT exactly when it
  exists (the same condition the MP tables use: a local APIC *and* an I/O
  APIC).
- **FADT (FACP)** -- revision 1, 116 bytes. SCI_INT 9; SMI_CMD B2h,
  ACPI_ENABLE F1h, ACPI_DISABLE F0h; PM1a_EVT_BLK/CNT_BLK/PM_TMR_BLK/GPE0_BLK
  all derived from PMBA (B000h); CENTURY names CMOS register 32h, which this
  codebase's RTC always implements.
- **FACS** -- 64 bytes, 16-byte aligned, everything zero: there is no S3
  waking vector.
- **MADT** -- one processor local APIC, the I/O APIC at FEC00000h, the
  IRQ0→GSI2 and IRQ9→GSI9 (level, active low) overrides, and the LINT1 NMI
  entry -- built from exactly the same facts `BiosRom.Mp.cs`'s MP table
  entries are, so the two can never describe a different machine.
- **DSDT** -- see below.

## The DSDT, and why there is no compiled ASL

There is no `iasl` on this build machine and no external tool dependency the
repository can add, so the DSDT is written directly in AML by two small C#
classes rather than compiled from a `.asl` source file:

- `Firmware/Acpi/AmlWriter.cs` encodes the handful of AML terms the
  namespace below needs -- Scope, Device, Processor, Name, Method, Package,
  Buffer, OperationRegion, Field -- plus the small set of executable
  operators (Store, And, Or, ShiftLeft, Decrement, FindSetRightBit,
  CreateWordField/CreateByteField, Return) the PCI interrupt links' method
  bodies use.
- `Firmware/Acpi/AcpiResources.cs` builds the raw resource-descriptor bytes
  (DWord Address Space, Word Bus Number Range, I/O Port, IRQ, End Tag) that
  sit inside a Buffer's own byte list.
- `Firmware/Acpi/AcpiTables.cs` assembles the tables above and the DSDT's
  namespace from those two.

The namespace:

```
\_S0   Package(4){0,0,0,0}
\_S5   Package(4){0,0,0,0}          -- the only sleep state; no \_S1/\_S3/\_S4
\_PR.CPU0                           -- Processor(), no P_BLK
\_SB.PCI0                           -- PNP0A03, _CRS: bus 0-0, I/O 0-CF7/D00-FFFF,
                                        one DWord memory range tracking the
                                        frame buffer and MMIO window
  .ISA                              -- the PIIX4 southbridge itself, _ADR 00010000h
    .DMAC .PIC .TIMR                -- PNP0200/0000/0100, no _CRS
    .RTC  .KBD  .MOU  .FDC  .COM1  .MATH   -- each with a fixed _CRS (I/O + IRQ)
    .LNKA .LNKB .LNKC .LNKD        -- PNP0C0F, _UID 1-4, _PRS/_CRS/_SRS/_DIS
                                        over an OperationRegion at 60h-63h
  ._PRT                             -- one entry per (device 2-5, pin A-D) plus
                                        PIIX4's own USB function, all pointing
                                        at PciInterruptRouter.Link's own answer
\_SB.PWRB                           -- PNP0C0C, the power button
```

COM2 and LPT are never emitted: this codebase does not fit a second serial
port or a parallel port device, so "only if fitted" (the PIIX4 datasheet's
own wording) is never true here. FDC's DMA channel (2) is not encoded as a
resource either -- no DMA descriptor tag is confirmed here, only named and
flagged as an open point (see "What is not settled" below).

`_CRS`/`_SRS`/`_DIS` on each link are the only methods with any logic at
all, and it is kept to a few opcodes: `_CRS` builds a fresh IRQ resource
template from the routed IRQ a `Field` reads out of the PIIX4's own 60h-63h
register; `_SRS` turns the OS's own resource buffer back into an IRQ number
with `FindSetRightBit` and writes it to the same `Field`; `_DIS` sets that
register's own disable bit without touching the IRQ it still names. None of
the three look at that disable bit when reporting the current resources --
a fully disabled link answering an empty template is out of scope for the
DOS/Win9x-era guests this BIOS targets.

## Runtime: `Devices/Chipset/Piix4PowerManagement.cs`

- **SMI_CMD (port B2h)**, registered only where the whole class exists (the
  i430TX board): F1h sets PM1_CNT.SCI_EN, F0h clears it. There is no SMM
  here to trap through instead -- the write acts on the bit directly.
- **The SCI**, IRQ 9, is recomputed after every write that could change it:
  `(PM1_STS & PM1_EN) | (GPE0_STS & GPE0_EN)`, asserted as a level line only
  while SCI_EN is set. POST marks IRQ 9 level-triggered in the ELCR
  (`Machine.RunPost`, i430TX board only) the same way it already does for
  the PCI IRQs, since the SCI is a shareable level line.
- **GPE0** -- STS and EN, two bytes each at PMBA+0Ch/+0Eh. Nothing in this
  codebase ever wires a general-purpose event to it, so STS always reads 0;
  EN is ordinary read/write storage all the same, since an ACPI OS is
  entitled to read back whatever it wrote there.
- **SLP_EN** (PM1_CNT bit 13) is a write-only strobe, still excluded from
  the persisted control register exactly as it always was, but now acted on
  once per write: with SLP_TYP = 0 (`\_S5`, the only sleep state the DSDT
  names) it calls `Machine.RequestPowerOff()`. Any other SLP_TYP an OS could
  still write (there being no `\_S1`/`\_S3`/`\_S4` for it to have asked for)
  does nothing.
- **The power button** is a *host* action, `Machine.PressPowerButton()`,
  distinct from the guest's own APM `RequestPowerOff`: while
  `Piix4Power.SciEnabled` (an ACPI OS has taken charge) it sets PWRBTN_STS
  and lets the SCI carry it to the guest's own driver; otherwise, and always
  on an i430FX board, it calls `RequestPowerOff()` directly, the same hard
  ATX soft-off the guest's own APM power-off already produces.
- **PMREGMISC** (offset 80h bit 0 of the power-management PCI function) is
  now enabled by `Machine.RunPost` itself, after `PciBus.Reset()` -- a real
  BIOS enables every one of the chipset's own fixed hardware register
  blocks during POST, and an ACPI OS trusts the FADT's own PM1a_EVT_BLK and
  its neighbours to already answer rather than negotiating this decode
  through PCI configuration space. Before this package nothing exercised
  the PM block through a real `Machine` at all, so this gap went unnoticed.

## Snapshot

The PM section (`"PIXP"`) gains a version 2: two more 16-bit fields, GPE0
STS and EN, after the existing 22 bytes. A version 1 file (from before this
package) still restores cleanly, with both fields put back at their reset
value of zero -- the same tolerance every other section added after the
container's own versioning gets. The tables themselves are firmware output,
rebuilt from the machine's own configuration on every boot, and keep no
snapshot section of their own.

## What is not settled

- The DWord Address Space and Word Bus Number Range descriptors' own
  General/Type-Specific Flags bytes are left at conservative defaults (0,
  or bit 0 "writable" for the memory window) rather than confirmed against
  a datasheet -- there is nothing PIIX4-specific about a plain PCI root
  bridge's own memory window, and the primary ACPI PDF would not fetch when
  this package was researched.
- The IRQ Descriptor's flags byte (Level, Active-Low, Shared = 18h) is
  recalled from the ACPI opcode tables rather than independently
  re-confirmed against the PIIX4 datasheet; it is cross-checked only
  against this codebase's own existing PIRQ wiring facts.
- FDC's DMA channel (2) is not encoded as an ACPI resource: no DMA
  descriptor tag is named among the resource encodings this package
  confirmed, so it is omitted rather than guessed.
- No S1/S3/S4, no HPET, no reset register (FADT 1.0 has none) -- explicitly
  out of scope (coordinator decision C6 in `plans/specs/acpi.md`).
- Not verified on real Windows 98/2000/XP or Linux ACPI code paths: no such
  guest could be booted in this environment. DOS 6.22 boots and reaches its
  command prompt unchanged on both chipsets, and the fast test suite and the
  protected-mode hardware vectors (6100/6100) are unaffected.

See `plans/specs/acpi.md` for the full register-level spec and the
coordinator's decisions (C1-C6), and `plans/specs/apic.md` for the MADT and
MP table facts this package builds both from.
