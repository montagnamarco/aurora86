# Aurora386

An Intel 80386/486 PC emulator written in C#, built to be read as much as to
be run. Every device is modelled the way the real silicon behaved, and the
comments explain *why* the hardware works that way, not just what the code
does. The target is the software of the early nineties: MS-DOS 6.22 and
Windows 3.11 run today; Windows 95 is the next goal, and the machine already
carries what it looks for first (a Plug and Play BIOS, ISA Plug and Play
cards, APM, an ATA disk on the IDE bus, the extended INT 13h functions).

## Status

**The processor.** Real mode, protected mode, virtual 8086 mode, paging,
task switching. Two suites generated from real hardware keep it honest:

| Suite | Result |
|---|---|
| [SingleStepTests/80386](https://github.com/SingleStepTests/80386), real mode | 1,758,490 of 1,758,700; the rest are cases where the silicon itself is not deterministic |
| [SingleStepTests_80386_protected](https://github.com/nand2mario/SingleStepTests_80386_protected) | 6100 of 6100 |

**The guests.**

- MS-DOS 6.22 installs from the original floppies and boots from the hard
  disk, with HIMEM, SMARTDRV, KEYB, MSCDEX and an ATAPI CD-ROM driver.
- Windows 3.11 reaches Program Manager in standard mode and in 386 enhanced
  mode, with a DOS window that opens and returns. Enhanced mode runs at about
  30 million instructions per second on an Apple M-series machine.
- An S3 Trio64V+ display driver runs at 800x600 and 1024x768 in 256 colours.
- The network works from DOS: with a packet driver and mTCP, DHCP gets an
  address, ping reaches the outside, HTGET fetches a page.

## What is emulated

| Component | State |
|---|---|
| 80386 / 486 CPU | Real, protected and V86 modes, paging, TSS; software x87 (`Firmware/Fpu`) |
| Memory bus | Up to 128 MB, A20 gate, unreal mode, linear frame buffer above the RAM |
| 8259 PIC (two), 8253 PIT, 8237 DMA, 8042 keyboard, CMOS/RTC | Complete |
| VGA | Text and planar graphics modes, mode 13h, DAC; VESA 2.0 with a linear frame buffer |
| S3 Trio64V+ | Extended CRTC registers, hardware cursor, 2D engine (BitBlt, fills, lines, pattern fills, pixels from the processor), MMIO at the LFB and in the A0000h window |
| BIOS | Written in C#, hooked from a real ROM image; INT 10h/13h/15h/16h/1Ah, extended INT 13h (41h-48h), APM 1.2, E801h/E820h |
| Plug and Play | PnP BIOS 1.0A with device nodes; ISA Plug and Play bus (isolation, CSN, configuration); APM power-off |
| Floppy and hard disks | Images up to 7.8 GB, translated geometry past 1024 cylinders, media change line |
| IDE | ATA hard disk as primary master, ATAPI CD-ROM as primary slave, a second disk on the secondary cable |
| CD-ROM | ISO 9660; a stand-in MSCDEX until the guest loads its own |
| Shared folders | Host folders as DOS drives through the INT 2Fh redirector |
| Sound Blaster 16 + OPL3 | DSP, mixer, DMA playback, FM synthesis; a Plug and Play card |
| NE2000 Ethernet | Registers, ring buffer, remote DMA; a user-mode NAT with DHCP, DNS, TCP, UDP and port forwarding |
| Mouse | Microsoft serial mouse on COM1, PS/2 mouse through the 8042, INT 33h driver in the BIOS |
| Snapshots | The whole machine to a file and back, including the redirector's attachment to DOS |

## Building and testing

Requires the .NET 10 SDK.

```
dotnet build -c Release
dotnet test tests/Aurora386.Tests --filter "Speed!=Slow"
```

The filter leaves out one test that boots DOS from a real image for three
billion instructions; without it the suite takes twelve minutes instead of
one. The hardware suite for protected mode runs from its own tool, against
the vectors of `SingleStepTests_80386_protected` (branch `master`, folder
`v1`):

```
dotnet run --project tools/HardwareSuite -c Release -- path/to/v1
```

## Running

The graphical front end (Avalonia) has a toolbar, drive lights with the
letter the guest gave each drive, menus for the mouse port and the network,
folders shared with the guest, snapshots, and a debugger with breakpoints,
watchpoints on memory and ports, a symbolic view of the ROM and the DOS
structures, and a disk inspector:

```
dotnet run --project src/Aurora386.Gui
```

The SDL front end takes everything from the command line, which is what a
script or a bug report wants:

```
dotnet run --project src/Aurora386.Sdl -- --hdd hdd.img
dotnet run --project src/Aurora386.Sdl -- --vm machines/dos --net nat --share ~/Documents
dotnet run --project src/Aurora386.Sdl -- --help
```

A machine lives in a folder with a `vm.json` describing its disks, memory,
mouse port, network and shared folders; the window and the command line open
the same file the same way. Disk images are not included: they are large
and generally covered by copyright. Point the emulator at your own.

## Installing MS-DOS

Create a disk, then boot the first installation floppy with it attached:

```
dotnet run --project src/Aurora386.Sdl -- --create-hdd hdd.img --size 500
dotnet run --project src/Aurora386.Sdl -- --hdd hdd.img --floppy dos622-1.img
```

When setup asks for the next disk, swap the image with the *Floppy A…*
button of the window: the BIOS reports the media change through its change
line, so DOS notices the new disk rather than trusting what it read from
the last one.

## Layout

```
src/Aurora386.Core     the machine: CPU, memory, devices, firmware, DOS redirector, NAT
src/Aurora386.Gui      the Avalonia window and the debugger
src/Aurora386.Sdl      the command-line front end
src/Aurora386.FatTool  reads and writes FAT images from the host
tools/HardwareSuite    runs the protected-mode hardware vectors against the CPU
tools/fat-insert.py    puts a file into a FAT16 image without booting the guest
tests/Aurora386.Tests  the unit tests, one class per device or service
docs/                  ARCHITECTURE.md, REAL-VS-PROTECTED-MODE.md, NETWORK.md
```

## Design notes

**An interpreter, not a JIT.** The goal is a machine you can read and step
through, so the CPU is a straightforward decode-and-execute loop with lazy
flags: the operands and the operation are recorded, and the flags computed
only when something reads them.

**The core is headless.** `Aurora386.Core` has no display or input
dependency: it exposes a framebuffer and accepts scancodes and mouse
movements. The whole machine is testable without a window, which is how
twelve hundred tests run in a minute.

**The BIOS is C#.** The ROM holds real entry points and interrupt vectors,
and each one is a two-byte hook the CPU recognises only inside the ROM; the
handler runs in the emulator. What the guest can observe -- the ROM's
bytes, the vectors, the OUTs to the interrupt controller -- is real, so a
protected-mode monitor like Windows' VMM sees a BIOS it can trap and
virtualise.

**Measure, do not guess.** Every guest-facing fix in the history came from
a trace: which subfunction DOS called and with what, which port the driver
touched, who wrote a byte. The tools for that are in the machine
(`DosCallLog`, `DiskWriteLog`, port and memory watchpoints, the
redirector's `Trace`) and in the debugger. `CONTRIBUTING.md` says more.

## Things the hardware does that surprise people

All real behaviours, found by testing against hardware traces and
documented in the source where they matter:

- **Scaled index with no index register.** In the SIB byte, when the index
  field says "none", the 386 still applies the scale factor to the *base*
  register. Later processors dropped this.
- **Unreal mode.** Returning from protected mode to real mode recomputes only
  the segment *base*. The *limit* survives in the descriptor cache, which is
  exactly how HIMEM.SYS reaches memory above 1 MB with ordinary instructions.
- **`INC` and `DEC` leave the carry flag alone**, which is what lets them
  appear inside multi-precision loops without destroying the running carry.
- **An IRET at ring 3 cannot change IOPL**, and Windows' DOS extender
  depends on it.
- **A 16-bit word read of the NE2000 data port in word mode consumes a whole
  word**, even when the driver reads it a byte at a time; the Crynwr packet
  driver reads its station address exactly that way.
- **DOS's INT 2Fh answers "invalid function" to every redirector call nobody
  serves.** A redirector that declines by leaving the registers alone is
  read as having succeeded, because the carry DOS passed in was clear.

## Licence

Not yet chosen. The test vectors belong to their authors: the real-mode
suite to the SingleStepTests project, the protected-mode suite to
nand2mario; both are used under their own terms and are not redistributed
here.
