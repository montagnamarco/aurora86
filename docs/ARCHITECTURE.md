# Aurora386 — how a PC works, and how we emulate it

This document explains two things side by side: **how an IBM-compatible PC
with a 386 is built**, and **how Aurora386 reproduces it in C#**. They are
kept together on purpose, because almost every decision in the emulator is
the consequence of a detail of the real hardware. The Italian original is
`ARCHITETTURA.md`; this version is the one kept current.

For the processor's modes -- real, protected, virtual 8086, paging -- there
is a separate guide, `REAL-VS-PROTECTED-MODE.md`, written around the bugs
that were found getting DOS and Windows to run.

---

## 1. The PC as a physical machine

A PC of 1990 is a processor attached to two buses: one for memory, one for
devices. Everything else follows from that.

```
                         ┌───────────────┐
                         │   386  CPU    │
                         └───┬───────┬───┘
                 memory bus  │       │  I/O bus
             ┌───────────────┘       └────────────────┐
             │                                        │
     ┌───────┴────────┐                    ┌──────────┴──────────┐
     │   RAM  +  ROM  │                    │      devices         │
     │                │                    │                      │
     │  0x00000 RAM   │                    │  0x20  PIC  (IRQs)   │
     │  0xA0000 video │◄── same memory     │  0x40  PIT  (time)   │
     │  0xC0000 ROM   │    as the device   │  0x60  8042 (keyboard)│
     │  0xF0000 BIOS  │                    │  0x70  CMOS (clock)  │
     └────────────────┘                    │  0x1F0 IDE  (disks)  │
                                           │  0x3C0 VGA  (video)  │
                                           └──────────────────────┘
```

Two different ways of talking to hardware:

- **Memory-mapped** -- the device *is* a range of addresses. The video card
  owns `0xA0000-0xBFFFF`: writing there means drawing.
- **I/O ports** -- a separate space of 65,536 ports, reached only by the
  `IN` and `OUT` instructions. The keyboard lives at port `0x60`.

So the 386 has **two distinct address spaces**: memory address `0x60` and
port `0x60` have nothing to do with each other. In the code they are
`PhysicalMemoryBus` (`Memory/`) and `IoBus` (`IO/`).

---

## 2. The real-mode memory map

The historical one-megabyte limit is not arbitrary: the 8086 had 20 address
lines, and every later PC had to stay compatible with that layout.

```
0x00000  ┌──────────────────────────────┐
         │ interrupt vector table       │  1 KB: 256 pointers of 4 bytes.
         │ (256 × 4 bytes)              │  INT 21h reads the address at 0x84.
0x00400  ├──────────────────────────────┤
         │ BIOS Data Area               │  256 bytes of state shared between
         │                              │  BIOS and operating system.
0x00500  ├──────────────────────────────┤
         │                              │
         │   conventional memory        │  The famous 640 KB. DOS, drivers and
         │   (DOS, drivers, programs)   │  the running program live here.
         │                              │
0x9FC00  ├──────────────────────────────┤
         │ extended BIOS data area      │  The last kilobyte: the PS/2 mouse
         │                              │  support keeps its state here.
0xA0000  ├──────────────────────────────┤
         │ graphics video memory        │  The VGA card, seen as memory.
0xB8000  ├──────────────────────────────┤
         │ text video memory            │  80×25 characters, 2 bytes each:
         │                              │  the character and its colour.
0xC0000  ├──────────────────────────────┤
         │ expansion ROMs               │  Video BIOS, SCSI controllers, LAN.
0xF0000  ├──────────────────────────────┤
         │ system BIOS                  │  64 KB. At power-on the processor
         │                              │  starts from here.
0x100000 ├──────────────────────────────┤
         │ extended memory (XMS)        │  Beyond the megabyte: reachable only
         │                              │  in protected or unreal mode.
         └──────────────────────────────┘
```

### Why video memory is "only" that region

There is no real card: there is a range of addresses the chipset diverts to
the device instead of to RAM. In Aurora386 the same mechanism is
`PhysicalMemoryBus`, which keeps, for every 4 KB page, either a pointer to
real RAM or a device to call:

```
write to 0xB8000
        │
        ▼
┌───────────────────┐   page 0xB8    ┌──────────────┐
│ PhysicalMemoryBus │───────────────►│  VgaMemory   │──► marks the row
└───────────────────┘   belongs to   └──────────────┘    as "redraw me"
        │               a device
        │ page 0x01
        ▼               is real RAM
   byte[] in memory
```

---

## 3. The processor: four modes of operation

The 386 is three processors in one, for compatibility's sake.

```
       ┌──────────────────────────────────────────────────┐
       │  REAL MODE                                       │
       │  address = segment × 16 + offset                 │
       │  1 MB reachable, no protection                   │
       │  How every PC powers up, to this day             │
       └────────────────────┬─────────────────────────────┘
                            │  set the PE bit of CR0
                            ▼
       ┌──────────────────────────────────────────────────┐
       │  PROTECTED MODE                                  │
       │  the segment is an index into a table of         │
       │  descriptors: base, limit, privileges            │
       │  4 GB, protected memory, four rings              │
       └────────┬──────────────────────────────┬──────────┘
                │ VM bit in the flags          │ PG bit of CR0
                ▼                              ▼
       ┌─────────────────┐        ┌──────────────────────┐
       │  VIRTUAL 8086   │        │  PAGING              │
       │  MODE           │        │  linear address      │
       │  a DOS inside   │        │  → tables → physical │
       │  a box          │        │  4 KB pages          │
       └─────────────────┘        └──────────────────────┘
```

And a fourth mode that is not in Intel's manuals:

> **Unreal mode.** Enter protected mode, load a segment with a 4 GB limit,
> go back to real mode. The limit **stays** in the hidden register, and from
> real mode you address 4 GB. Undocumented, a side effect of the silicon --
> and `HIMEM.SYS` leans on it. Aurora386 reproduces it: see
> `REAL-VS-PROTECTED-MODE.md`, or the tests in `UnrealModeTests`.

### How Aurora386 emulates it

An **interpreter**, not a JIT compiler: read an instruction, execute it, move
to the next. Slower than a translator, but transparent to verify -- and for a
machine of 1990 the speed is enough (30 to 55 million instructions per
second on a laptop, against the 8 or so a real 25 MHz 386 managed).

```
src/Aurora386.Core/Cpu/
├── Cpu.cs             state, registers, memory access, fetch
├── Cpu.Execute.cs     one-byte opcodes (the bulk of the table), fault delivery
├── Cpu.TwoByte.cs     0F xx opcodes: protected mode, bit ops, LAR/LSL
├── Cpu.ModRm.cs       operand and address decoding
├── Cpu.Protected.cs   descriptors, gates, privilege changes, IRET
├── Cpu.Paging.cs      page tables and TLB
├── Cpu.Strings.cs     MOVS, STOS, SCAS and the repeat prefixes
├── Cpu.Shifts.cs      rotates and shifts
├── Cpu.Bcd.cs         decimal arithmetic (AAA, DAA…)
├── Cpu.Groups.cs      the opcode groups (80-83, FE/FF, …)
└── LazyFlags.cs       flags computed only when somebody reads them
```

**Lazy flags** deserve a note. Every arithmetic operation should update six
flags, but almost none of them is ever read. `LazyFlags` keeps the operands
and computes a flag only when asked: the single optimisation that matters
most in an interpreter. It also has a trap: `INC` and `DEC` are the only
instructions that *inherit* the carry instead of computing it, and they must
read it from the materialised state, not from the stale one.

**Faults are not exceptions.** A fault on a real 386 costs a few cycles; a
C# `throw` costs eight microseconds. Windows in enhanced mode raises about a
million faults per 300 million instructions -- its monitor traps every `CLI`,
port access and `INT n` from ring 3, and uses `ARPL` as a breakpoint in its
V86 stubs. The hot paths therefore deliver the fault directly (`FaultNow`)
instead of unwinding the host stack.

---

## 4. Interrupts: how the hardware asks for attention

A key press cannot be "noticed" by the processor: it is the device that
interrupts the running program.

```
  keyboard          8259 PIC              CPU
     │                  │                  │
     │  key pressed     │                  │
     ├─────────────────►│                  │
     │                  │   IRQ 1          │
     │                  ├─────────────────►│  finishes the instruction
     │                  │                  │  saves CS:IP and the flags
     │                  │◄─────────────────┤  asks: which vector?
     │                  │   vector 09h     │
     │                  ├─────────────────►│  jumps to [0x09 × 4]
     │                  │                  │
     │                  │                  ▼
     │                  │           keyboard handler
     │                  │           reads port 0x60
     │                  │           puts the key in the buffer
     │                  │◄──────────  OUT 0x20, 0x20  (end of interrupt)
     │                  │                  │
     │                  │                  ▼  IRET: the program resumes
```

Two PICs in cascade, because one handled eight lines and that was not enough:

```
   master PIC (0x20)             slave PIC (0xA0)
   ├── IRQ 0  timer              ├── IRQ  8  real-time clock
   ├── IRQ 1  keyboard           ├── IRQ  9
   ├── IRQ 2  ─────────────────► ├── IRQ 10
   ├── IRQ 3  serial 2           ├── IRQ 11
   ├── IRQ 4  serial 1 (mouse)   ├── IRQ 12  PS/2 mouse
   ├── IRQ 5                     ├── IRQ 13  coprocessor error
   ├── IRQ 6  floppy             ├── IRQ 14  IDE disk
   └── IRQ 7  printer            └── IRQ 15
```

IRQ 2 does not exist as a usable line: it is the wire the second PIC hangs
from. A device that claims "IRQ 2" really uses IRQ 9. An interrupt of equal
or lower priority than one already in service waits until the handler sends
its EOI -- that is what stops a handler from being re-entered by its own
line, and it is why every ROM interrupt entry ends with `OUT 20h`.

---

## 5. The BIOS: the main architectural choice

An emulator can load an original ROM, or write its own BIOS. Aurora386 does
the latter, and in a particular way.

The emulated ROM contains **three-byte sequences**:

```
   0F FF 13     ← not a 386 instruction: a hook
   CF           ← IRET
```

When the CPU meets `0F FF` *inside the ROM*, it calls a C# function; the next
byte says which. The BIOS is therefore written in C#, but **the guest reaches
it through the ordinary interrupt vectors**, without noticing anything.

```
   DOS program
        │  INT 13h  (read a sector)
        ▼
   vector 0x13 ──► F000:1030 in the ROM
                         │
                         │  0F FF 13
                         ▼
                  ┌───────────────────────────┐
                  │ C#: DiskServices.Handle() │  reads from the disk image
                  └───────────────────────────┘  and writes into guest memory
                         │
                         ▼  CF (IRET)
                    back to the program
```

The advantage is being able to put an observation point anywhere; the cost
is having to implement every service the guest expects to find.

Three rules came out of Windows, and they are worth knowing before touching
`BiosRom.cs`:

- **The hook is recognised only in the ROM.** `0F FF` is a deliberate
  undefined opcode elsewhere -- KRNL386 executes it to see whether a
  debugger is present -- and it must raise `#UD` there.
- **Anything the guest's monitor needs to see must be a real instruction in
  the ROM.** Windows' VPICD virtualises the interrupt controllers by trapping
  port writes, so the EOI has to be an `OUT 20h` executed by the guest, not a
  C# call; the same goes for `INT 1Ch` from the timer tick, the `IN 60h` of
  the keyboard and mouse handlers, and the VGA register writes of a mode set,
  which the VDD shadows.
- **Vectors DOS parks on an IRET are reclaimed.** IO.SYS points INT 33h at
  an IRET while it initialises; the machine notices and puts the mouse
  driver back, but leaves alone a vector that leads to real code.

### What lives where

```
src/Aurora386.Core/Firmware/
├── BiosRom.cs                 builds the ROM image and the hooks
├── BiosDataArea.cs            the 256 bytes of state at 0x400
├── DiskServices.cs            INT 13h — disks
├── VideoServices.cs           INT 10h — video
├── KeyboardServices.cs        INT 16h — keyboard
├── MouseServices.cs           INT 33h — the mouse driver interface
├── PointingDeviceServices.cs  INT 15h C2xx — the PS/2 mouse BIOS
├── CompactDiscServices.cs     INT 2Fh 15xx — MSCDEX, when the guest has none
├── XmsManager.cs              extended memory (what HIMEM.SYS offers)
└── Fpu/
    ├── FpuEmulator.cs         software coprocessor, on vector 7
    └── Extended80.cs          the 80-bit format of the 80387
```

Machine.Handlers.cs holds the native halves too small for a class of their
own: the hook dispatcher, the timer tick, INT 2Fh, INT 15h, the time of day.

---

## 6. The coprocessor that is not there

This machine has no 80387, and that was historically common: 386SX systems
were sold without one. Intel's mechanism for the case is elegant.

```
   the program executes:  FADD  qword [bx]
        │
        ▼
   the CPU checks CR0.EM ──► it is 1: no coprocessor
        │
        ▼
   exception 7 "device not available"
        │
        ▼
   vector 7 ──► FpuEmulator.Handle()
                      │
                      │  1. reads CS:IP from the stack
                      │  2. re-reads the instruction from guest memory
                      │  3. decodes it again (prefixes, ModRM, address)
                      │  4. does the arithmetic
                      │  5. ADVANCES IP past the instruction  ← without this,
                      ▼                                          an endless loop
                 back to the program
```

Step 5 is what makes the mechanism delicate: the fault reports the address
of the instruction, not the next one. A handler that merely returns puts the
CPU back on the same instruction, which faults again, forever.

The registers are a **stack of eight** with a rotating top:

```
   FLD 2.5           FLD 4.0          FADD
   ┌──────┐          ┌──────┐         ┌──────┐
   │ 2.5  │◄ST(0)    │ 4.0  │◄ST(0)   │ 6.5  │◄ST(0)
   ├──────┤          ├──────┤         ├──────┤
   │      │          │ 2.5  │ ST(1)   │      │
   └──────┘          └──────┘         └──────┘
```

Values are kept at **true 80 bits** (`Extended80`): a 64-bit mantissa,
against the 53 of a `double`. The difference shows only in long chains of
arithmetic, but it is the one the 387 had. The plan is to turn this into a
real instruction-level x87 -- a 487 once the CPU becomes a 486.

---

## 7. Disks: two different roads

```
  ┌─────────────────────────────────────────────────────────┐
  │  hard disk and floppy                                    │
  │                                                          │
  │  program ──INT 13h──► BIOS ──► DiskImage ──► .img file   │
  │                                                          │
  │  CHS addressing: cylinder / head / sector                │
  │  512-byte sectors                                        │
  └─────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────┐
  │  CD-ROM                                                  │
  │                                                          │
  │  program ──INT 2Fh──► MSCDEX ──► driver MSCD001          │
  │                                        │                 │
  │                            ports 0x1F0 (ATAPI)           │
  │                                        │                 │
  │                                   IdeController          │
  │                                        │                 │
  │                                   IsoImage ──► .iso file │
  │                                                          │
  │  2048-byte sectors, addressed by block                   │
  │  SCSI commands inside ATAPI packets                      │
  └─────────────────────────────────────────────────────────┘
```

A CD is not a bigger disk: the sector size, the addressing and the protocol
all change. Pretending it were a `DiskImage` would have shown at the first
`DIR`. The emulator carries a stand-in MSCDEX for a guest that has none; the
moment a real driver (OAKCDROM, say) sends its first ATAPI packet, the
stand-in and the redirector's copy of the disc both step aside, so that the
disc has exactly one drive letter -- the driver's. Which letter that is, the
emulator asks the guest (INT 2Fh AX=1500h) and shows in the status bar.

### The third road: the same disk through the IDE registers

The hard disk is reachable twice. DOS goes through INT 13h, as above.
Windows -- the 32-bit disk access of 3.1, the protected-mode driver of 95
-- bypasses the BIOS and drives the ATA registers at 0x1F0 directly:
IDENTIFY DEVICE, READ/WRITE SECTORS one interrupt per sector, READ/WRITE
MULTIPLE one per block, addresses as cylinder/head/sector or as a 28-bit
sector number. `IdeController.Disk.cs` answers as the master of the primary
cable, in front of the CD-ROM as slave, out of the same `DiskImage` the BIOS
uses; a second disk gets the secondary cable (0x170, IRQ 15). Both doors
open onto the same file, so what one writes the other reads.

### Geometry, presented and real

The BIOS's CHS interface has ten bits for the cylinder, so a disk past 1024
cylinders (504 MB with 16 heads) is *presented* with the cylinders halved
and the heads doubled, as many times as it takes, up to 255 heads -- the
translation every BIOS of the mid-nineties did. The image keeps its real
geometry; `DiskServices.PresentedGeometry` is what INT 13h 08h reports and
what 02h/03h decode with, and the same presented geometry goes into the MBR
and the BPB of every disk the emulator creates, because DOS turns sector
numbers into CHS with the heads and sectors it reads from the BPB. The
extended functions (41h-48h) address by sector number and need none of
this; Windows 95 uses them when they exist.

### The ATAPI protocol, which is where the defects hide

```
   the driver writes a 12-byte SCSI packet
        │
        ▼
   the device raises DRQ and asserts an interrupt   ← first interrupt
        │
        ▼
   the driver reads the data from port 0x1F0
        │
        ▼
   the device drops DRQ and asserts an interrupt    ← second interrupt
```

**Two interrupts per command**, not one. A driver that expects two and gets
one sits there, without reporting anything. A disc changed after boot is
announced once as *unit attention* (sense 06h/28h), which is how the driver
learns to re-read the directory.

---

## 8. The video

The VGA card has no single format: there are at least three ways of reading
the same addresses.

```
  text mode (0x03)               graphics mode 13h           16-colour modes
  0xB8000                        0xA0000                     0xA0000
  ┌────┬────┬────┬────┐          ┌──────────────┐            ┌──────────────┐
  │ 'A'│attr│ 'B'│attr│          │ 1 byte =     │            │ 4 planes     │
  └────┴────┴────┴────┘          │ 1 pixel      │            │ superimposed │
   2 bytes per character         │ 320×200      │            │ 1 bit per    │
   80×25 = 4000 bytes            └──────────────┘            │ plane/pixel  │
                                                              └──────────────┘
```

The 16-colour mode is the most contorted: four **bit planes** stacked at the
same address, each pixel taking one bit from each. Writing a byte touches
eight pixels in one plane. It was the way to have 16 colours without
quadrupling the memory.

The character generator is **RAM, not ROM**: `MODE CON CP PREPARE` loads a
different font, and that is how Italian DOS gets its accented letters.

Two things learnt from Windows: **the registers, not the BIOS mode number,
say what the screen shows.** The VDD hands the display back to the Windows
VM by rewriting the VGA registers and the four planes, never through
`INT 10h`; renderer and memory decoding both read `EffectiveMode`, derived
from the graphics controller and the CRTC. And **the mouse pointer is
composed over the frame** (`MousePointer`), not written into video memory
as the DOS driver did, so that a program reading the screen back never finds
the pointer in its text.

### The S3 engine: a card that draws, and three doors into it

An SVGA driver does not paint pixels through the 64 KB window. It tells the
card where a rectangle is and the card fills it; the same for a copy (which
is what scrolling is), a line, or an 8x8 brush tiled across a window's
background. The registers are the IBM 8514/A's -- `CUR_X`, `CUR_Y`, the two
axis counts, the mixes, the command at port `9AE8h` -- and every S3 kept
them, with the same operation codes: 1 line, 2 rectangle, 6 copy, 7 pattern.

The driver reaches them by three doors, and Windows' driver uses the least
obvious one:

```
   ports 82E8h..E2E8h            OUT from ring 3: a fault the monitor
                                  has to catch -- slow

   LFB + 16 MB                   memory-mapped, the Trio64V+ way

   A8000h..AFFFFh with CR53.4    memory-mapped, the Trio64 way: the upper
                                  half of the VGA window becomes registers
                                  (packed pairs at 8100h: one 32-bit write
                                  sets X and Y), the lower half becomes the
                                  image transfer area
```

Two lessons paid for in tracing. First, **pixels can come from the
processor**: with bit 8 of the command set the engine takes the rectangle
and waits for data -- each bit a foreground-or-background choice for a
glyph, each byte a pixel for a brush -- written *after* the command, with
`REP MOVSD` into the transfer area. Second, the card is only half of it:
**the monitor decides which pages the driver can even reach.** Windows'
S3 VDD maps a page of the A0000h-BFFFFh region to the hardware only if its
own shadow of graphics controller register 6 says the card decodes it, and
the shadow is fed exclusively by trapped `OUT`s. A BIOS that set a VBE mode
natively, without one `OUT`, left the shadow saying "text at B8000h", and
the driver's writes to A8000h landed on the VMM's null page. The ROM now
programs the standard registers through the ports for `4F02h` too, as a
card's BIOS does (`Entry.VesaProgram`).

---

## 9. DOS as the emulator sees it

Aurora386 runs a **real MS-DOS 6.22**, not an imitation of it. `INT 21h` is
handled by the DOS inside the emulated machine.

There is one point where the emulator inserts itself: the **redirector**,
the same interface with which Novell and Microsoft networks mounted remote
drives.

```
   DIR E:
     │
     ▼
   DOS ──► is drive E: a redirector's? ──► INT 2Fh, function 11h
                                                    │
                                                    ▼
                                          RedirectorServices (C#)
                                                    │
                                    ┌───────────────┴───────────────┐
                                    ▼                               ▼
                          IsoFileSystem                  HostDirectoryFileSystem
                          (the CD, by name)              (a folder on the host)
```

It is the mechanism that lets a folder of the host appear as a DOS drive,
without inventing calls DOS does not know.

The protocol was never documented by Microsoft, and every one of its
conventions is a number that nothing checks: arguments in the Swappable
Data Area at fixed offsets (the search attribute at 24Dh, the extended
open's action at 2DDh), the drive identified by pointer arithmetic on the
CDS array, the "remote" mark as bit 15 of the SFT's device-information
word, "declined" spelt as carry set and AX=1 exactly as DOS's own handler
would. Get one wrong and DOS does not complain; it walks the FAT of the
wrong disk, or refuses a write before asking, or lists a file as the volume
label. `RedirectorProtocolTests` pins each of them down, and section 13
says how they were found. The attachment to DOS -- where the SDA and CDS
are, which vector was displaced -- travels with a snapshot, because a guest
restored without it has a hooked vector in memory and a redirector that
knows nothing.

---

## 10. Project layout

```
Aurora386
│
├── src/Aurora386.Core        ← the heart, with no user interface
│   ├── Machine*.cs           the assembled PC: construction, POST, run
│   │                         loop, minor native handlers, media
│   ├── Cpu/                  the processor (16 partial files of Cpu)
│   ├── Memory/               paged memory bus, A20
│   ├── IO/                   port bus
│   ├── Firmware/             the BIOS only: ROM with hooks, BDA, services
│   │   │                     for video/disk/keyboard/mouse/CD, XMS
│   │   └── Fpu/              the trap-based coprocessor (80-bit)
│   ├── Devices/              emulated hardware, one folder per role
│   │   ├── Interrupts/       8259 (master and slave)
│   │   ├── Timing/           8253, CMOS/RTC
│   │   ├── Input/            8042, PS/2 and serial mouse, mouse state
│   │   ├── Storage/          disk and ISO images, IDE/ATAPI controller
│   │   ├── System/           DMA 8237, system ports (61h, 92h)
│   │   ├── Video/            VGA: registers, memory, fonts, renderer, pointer
│   │   ├── Audio/            Sound Blaster 16, OPL3, mixer
│   │   ├── Network/          NE2000, loopback, and Nat/ (the user-mode NAT)
│   │   └── IsaPnp/           the ISA Plug and Play bus and the cards on it
│   ├── Dos/                  redirector and file systems (host, ISO)
│   ├── Configuration/        vm.json: one machine described in a file
│   └── Diagnostics/          disassembler, DOS call log, disk write log
│
├── src/Aurora386.Gui         Avalonia interface (window, menus, debugger)
├── src/Aurora386.Sdl         alternative SDL2 frontend
├── src/Aurora386.FatTool     tool for injecting files into images
├── tools/HardwareSuite       runs the protected-mode hardware vectors
└── tests/Aurora386.Tests     1,200 tests
```

The rule is **one folder per role, and the namespace follows the folder**:
`Aurora386.Core.Devices.Storage`, `Aurora386.Core.Firmware`, and so on. The
firmware contains no devices and the devices contain no firmware: a disk
image is a piece of hardware (`Devices/Storage`), the INT 13h service that
uses it is firmware (`Firmware`).

**The core does not depend on the interface.** That is what makes tests like
this possible: a `Machine` is built, run for a precise number of
instructions, and examined -- without opening a window.

```csharp
using var machine = new Machine();
machine.AttachDisk(DiskImage.OpenHardDisk("hdd.img"));
machine.RunInstructions(3_000_000_000);
// and now read the video memory to see what is on screen
```

---

## 11. The main loop

```
   RunInstructions(n)
        │
        ▼
   ┌──────────────────────────────────────────┐
   │  repeat in batches of 2000 instructions: │
   │                                          │
   │    ┌──────────────────────────────┐      │
   │    │ 2000 × Cpu.StepWithInterrupts│      │
   │    └──────────────┬───────────────┘      │
   │                   ▼                      │
   │    ┌──────────────────────────────┐      │
   │    │ AdvanceDevices(2000)         │      │
   │    │  timer, keyboard, mouse,     │      │
   │    │  disk, audio, network        │      │
   │    └──────────────┬───────────────┘      │
   │                   ▼                      │
   │      pending events, vectors, reset      │
   └──────────────────────────────────────────┘
```

Devices do not advance at every instruction: that would be correct and very
slow. They advance in batches, told how many instructions have passed. The
batch is short enough not to lose interrupts and long enough not to weigh.
Time itself is counted in instructions against a declared CPU frequency
(`CpuFrequencyHz`, 25 MHz), which is what makes a run reproducible: the same
image and the same keystrokes give the same trace every time.

A detail you pay for if you get it wrong: when the CPU is in `HLT` the batch
stops, **but the devices advance anyway**. Only an interrupt can wake the CPU
up, and only the devices can produce one: skipping that step would freeze
the machine forever.

---

## 12. How it is verified

The project has **1,200 unit tests** (`dotnet test --filter "Speed!=Slow"`
leaves out the one that boots DOS from an image and takes twelve minutes)
and runs the **SingleStepTests** suite for the 80386 -- 6,100 protected-mode
cases captured from a real chip, run by `tools/HardwareSuite`, and 1.76
million real-mode ones -- after every change to the CPU. Some principles:

**Test through the machine, not around it.** The FPU tests assemble real
instruction bytes and let the CPU fault; they do not call the emulator
directly. Decoding is half the work, and a test that skipped it would pass
even while reading the operand from the wrong address.

**Check that the test knows how to fail.** After every fix the defect is
reintroduced and the tests must go red. More than once this revealed a test
that measured nothing: one on the write log passed even with sabotaged code,
because an off-by-one cluster still fell inside the same file. It was
rewritten using two adjacent files.

**Prefer a case that discriminates.** `CONFIG.SYS` and `AUTOEXEC.BAT` occupy
adjacent clusters: being off by one means naming the wrong file, not being
"a little imprecise".

**Measure, do not deduce.** Every defect Windows exposed -- ARPL as a
breakpoint, the VM bit, IOPL in IRET, the word across a page, the video mode
decided by a BIOS byte -- came out of a headless harness that reproduced the
symptom and logged the guest, not from reasoning about a disassembly. The
harnesses boot an image with `RunInstructions`, never from a snapshot, type
through the keyboard controller, and watch `ProcessorException`,
`SoftwareInterruptIssued`, `TripleFault` and the framebuffer.

---

## 13. Diagnostic tools

Two logs, switched on by environment variable because they cost too much to
keep always on:

```bash
AURORA_WRITELOG=/tmp/writes.log   # the files the guest writes, by name
AURORA_DOSLOG=/tmp/dos.log        # rename, exec, create, delete
```

The first resolves every written sector to the file that owns it, by reading
the image's FAT tables: the trace does not say "sector 41328", it says
`C:\WINDOWS\SYSTEM\VGA.DRV`. The second reads the names from guest memory at
the moment of the call, because that is the only place they exist -- DOS
receives a pointer, not a name.

The debugger window in the GUI shows the disassembly, the registers, memory
and stack, with breakpoints (conditional too), watchpoints on memory and on
ports, the descriptor tables and the I/O bitmap of the TSS, the ROM entry
points and the DOS structures by name, and a FAT-aware view of the disks.

For a headless bench the machine itself carries the instruments, and they
are what found every redirector defect: `Cpu.SoftwareInterruptIssued` for
every INT with its registers, `Io.PortAccessed` for every port, `Memory.
SetWatchRanges` with `Watched` for who writes a byte (with CS:IP at that
moment), `Cpu.ProcessorException` for every fault, `Redirector.Trace` and
`TraceResult` for every redirector call and its answer, and `MachineSnapshot`
to freeze the machine at the prompt once and restore it in a second for
every experiment after. `Cpu.PeekLinear` reads through the page tables
without disturbing anything, which is what a guest in enhanced mode needs.

---

## 14. The network: a NAT made of sockets

The NE2000 is emulated faithfully down to its ring buffer, because a
driver can observe all of that; where the frames go once they leave the
card, no guest can see, so the wire is an interface (`INetworkBackend`)
with two implementations. The loopback hands every frame straight back,
which is enough for a driver's self-test. The NAT (`Devices/Network/Nat`)
is a small TCP/IP stack: to the card it is a router at 10.0.2.2 that
answers ARP, hands out 10.0.2.15 by DHCP, resolves names through the
host's own resolver as 10.0.2.3, and turns every TCP connection and UDP
exchange into an ordinary socket the host opens on the guest's behalf. TCP
is *terminated*, not forwarded: the guest talks TCP to what it believes is
the remote host, and `NatTcpConnection` plays that host -- sequence
numbers, windows, retransmission -- while the bytes go through a connected
socket. It needs no privileges and reaches wherever the host does; the
price is that nothing outside can open a connection to the guest unless a
port is forwarded. The sockets live behind `IHostNetwork`, so the whole
stack is tested against a fake host, segment by segment. `NETWORK.md` has
the details.

---

## 15. What Windows 95 asks first

Windows 95 enumerates the machine three ways and expects all three to
answer, and the machine already does, ahead of the guest itself:

- **The PnP BIOS** (`Firmware/PnpBios.cs`): a `$PnP` header in the F000h
  segment and an entry point called *far*, with its arguments on the stack
  in the C convention, from real mode and from 16-bit protected mode with
  the same code. It lists the board's devices as nodes with their
  resources in the ISA Plug and Play tag format: interrupt controllers,
  timer, DMA, keyboard, mouse port, COM1, both IDE channels, VGA, memory.
- **ISA Plug and Play** (`Devices/IsaPnp`): the protocol on three ports by
  which cards that start deaf are woken with a 32-byte key, isolated one
  at a time by shifting out their 72-bit serial numbers, numbered, read
  for what they can accept and told where to go. The Sound Blaster and the
  NE2000 are such cards; they start active at their classic addresses so
  DOS finds them, and move if configured.
- **APM 1.2** (`Firmware/ApmBios.cs`): reached by INT 15h and by a
  protected-mode entry in ROM; the one call that does something is the one
  that turns everything off, which the front ends treat as the switch.

The BIOS also answers INT 15h E801h and E820h, the memory map beyond the
64 MB ceiling of the old call, and the extended INT 13h functions.

The PCI bus (`Devices/Pci`) now provides CF8h/CFC configuration cycles,
minimal host/ISA bridges and compatibility IDE. `Firmware/PciBios.cs`
exposes enumeration and configuration through INT 1Ah and BIOS32 far calls.
The IDE command register controls the actual legacy port mappings. PCI
interrupt routing and Windows 95 guest validation remain open; see
[PCI configuration and firmware](PCI.md) for the implemented boundary.
