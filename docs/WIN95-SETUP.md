# Diagnosing the Windows 95 SETUPX fault

## What works

A Windows 95 OSR2.5 setup, run on an Intel 486 with 16 MB RAM through a
DOS 6.22 boot disk with HIMEM and EMM386, completes hardware detection,
driver installation (including Plug-and-Play detection of a Sound Blaster
at `Enum\ISAPNP\CTL0044` and an RTL8019 network adapter), APM
initialization, and PnP configuration, reaching "Setup has finished
configuring your system." A restarted guest logs in through the serial
mouse, reaches the desktop with icons and taskbar, and can run a full DOS
session from the desktop (`ver`, `dir`, a file copy, `EXIT`) and return
correctly. Optional Messaging and printer setup, and the OSR2.5 network
file copy, are not exercised by these checks.

## What the emulator had to get right

Eight separate defects blocked this path from a fresh DOS boot to a working
desktop, each with its own register- or instruction-level rule and
regression test.

**SETUPX.DLL general protection fault (RETF descriptor checks).** A far
return must validate the destination code descriptor's type, privilege and
presence before checking its limit, not after. `SETUPX.DLL` returned into a
not-present code segment, which Windows 95 expects to trap as `#NP` so it
can reload the segment; checking the limit first raised `#GP` with error
code 0 instead, and Setup aborted with a `SETUPX.DLL` general protection
fault. The fix does not change CS, and a fault restores the original stack
pointer so the instruction can be retried. This is the order specified for
same-level returns in the
[Intel 80386 RET reference](https://www.ardent-tool.com/CPU/docs/Intel/386/manuals/prref386/RET.htm).
`ProtectedModeTests.FarReturnValidatesTheDescriptorBeforeItsLimit` exercises
16-bit and 32-bit returns with an absent code descriptor, a wrong
descriptor type, and a present code segment with an insufficient limit,
checking both the exception and the preserved return frame.

**SYSDETMG.DLL stack fault during hardware detection (interlevel RETF
parameter copy).** An interlevel `RETF n` through a call gate must skip the
parameters the call gate copied onto the inner stack before loading the
saved outer SS:SP, then discard `n` bytes from the outer stack as well.
Omitting that skip popped the copied parameter bytes as if they were the
outer SP and SS, corrupting the return during IDE/ESDI hardware detection.
`FarReturnDiscardsCopiedParametersBeforeRestoringTheOuterStack` covers both
operand sizes; a further regression checks that a rejected return leaves
CS, SS and privilege unchanged rather than partially updated.

**VPOWERD / APM BIOS carry-flag corruption.** The APM BIOS entry point must
report failure through the live carry flag, and only copy that flag into
the stacked FLAGS word for an INT 15h-style entry. Treating every call
(real-mode INT and 32-bit far call alike) as an INT 15h frame instead wrote
the carry bit into the word at ESP+4: for a 32-bit far call that word is
the return code selector, so reporting "function not supported" corrupted
`0028h` into `0029h` and produced `#GP(0029h)` on the following `RETF`
("While initializing device VPOWERD: Windows protection error"). Four tests
execute real 16-bit and 32-bit far calls, covering success and
unsupported-function failure, and check carry, return selector, stack
pointer and a caller-stack sentinel.

**DSP ASP command polling stall.** The Sound Blaster driver's
Plug-and-Play detection probes optional ASP-chip registers with DSP command
`08h`, subcommand `03h`, and expects `FFh` ("no ASP chip present") on a card
without one. The emulated DSP answered nothing, so the driver's own timeout
loop -- which reloads only the low half of its counter but decrements the
full register -- spun for billions of instructions waiting for a reply that
would never come. The DSP now consumes the arguments of ASP mode/codec/
register writes and answers register probes with read-only compatibility
values, following the
[reference implementation and hardware observations in DOSBox-X](https://dosbox-x.com/doxygen/html/sblaster_8cpp_source.html);
it does not implement an ASP audio processor or its microcode. Five
port-level tests cover the measured command sequence, argument framing for
the three write commands, and the absent-chip version response.

**REGSVR32 page fault (PUSH must not commit SP before the write
completes).** `PUSH` must compute its destination address, write the
value, and only then commit SP or ESP -- not lower SP/ESP and then write.
Lowering the pointer first meant that a page fault on the write, followed
by a second write-protection fault on the retried write, lowered SP twice:
the stack ended eight bytes below its intended position, and the following
`RET` read a pushed argument as its return address. Four tests execute
`PUSH`, service its page fault, return with `IRETD` and retry, checking the
original stack pointer and the pushed value for both operand widths and
both stack widths.

**Serial mouse state must be part of the machine snapshot.** Snapshots
must capture the serial mouse's UART registers, queued receive bytes,
pending serial movement, and both the current and last-reported button
states, or restoring a snapshot leaves the mouse unable to continue a
movement or button report already in progress. Tests restore this state
into a fresh machine and verify the remaining packet bytes, IRQ state, and
subsequent reports. Snapshots taken before this change remain readable but
cannot reconstruct serial-port state they never captured.

**DOS text services under a virtualized display.** When the display is
virtualized, INT 10h AH=00h (mode set) must update the guest BDA, text
page, palette, cursor and metadata through the same guest-visible path a
real BIOS uses, without writing the physical display directly; the other
observed services (AH=02h, 03h, 08h, 0Eh, 0Fh) must read and write through
the guest's own BDA, paging and VGA ports. This is what lets a full-screen
DOS session opened from the Windows 95 desktop print correctly and return
without corrupting display state. Focused BIOS text-service tests cover
mode changes, cursor movement and character output through this path.

**CD ownership must survive a snapshot round trip.** Restoring a snapshot
taken after a native CD driver (MSCDEX) has taken ownership of the emulated
CD redirector must preserve that ownership; previously, restoring reset the
ownership flag, so the redirector reattached and a following ATAPI command
could remove the mount out from under the guest driver. New snapshots
retain CD ownership: the host mount is reconciled before RAM is restored,
preserving the guest CDS bytes. Tests cover in-memory and file round trips,
returning to the emulated CD services and loading the previous snapshot
format; snapshots taken before this change remain readable but cannot
supply ownership information they never recorded.

**Diskette drive type must not depend on inserted media.** INT 13h
functions 08h and 15h previously treated a floppy drive with no image
inserted as an absent drive, even when CMOS described it as a 1.44 MB
drive; Windows 95 then reported the wrong drive type ("5¼ Floppy" instead
of "3½ Floppy"). Disk services now retain diskette geometry independently
of media: attaching an image selects the drive geometry as before, and
ejecting it preserves that description; CMOS and the BIOS equipment count
follow the installed drives, not the inserted media. This follows the model
in SeaBIOS's
[`disk_1308`](https://github.com/coreboot/seabios/blob/master/src/disk.c),
which reports installed drive geometry without reading the medium.

## Known limits

- Cold-restoring a hardware-detection checkpoint into a fresh machine that
  already owns the CD through MSCDEX can produce a `SU-0014` setup error
  ("error accessing `D:\*.*`"); the interaction between a fresh machine's CD
  redirector and a guest that already owns the CD has not been fully
  characterized, independently of the CD-ownership snapshot fix above.
- Random text glyphs and attributes have been observed on the Trio video
  model before ScanDisk runs during a Windows 95 boot; this reproduces the
  startup glyph symptom tracked in the
  [Trio guest regression matrix](TRIO-REGRESSION.md), whose cause is
  unresolved.
- A separately reported `spool32.exe` error with an unresponsive Close
  button has not been reproduced; a `regsvr32.exe` error seen instead
  during the same setup stage is explained by the PUSH defect above.
- The Clouds wallpaper produces a blue preview and a blue desktop with
  repeating artifacts on the emulated Trio driver; Carved Stone (tiled) and
  wallpaper removal work correctly, and copying `CLOUDS.BMP` through
  Explorer's Copy/Paste over the native IDE driver reproduces the file byte
  for byte. The faulty graphics operation has not been identified, and
  other storage defects have not been ruled out.

## Reproducing the guest check

Build and launch the interactive probe against your own Windows 95 OSR2.5
CD image and a DOS 6.22 boot disk:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  <path-to-your-disk-image> <path-to-your-Win95-iso> <output-folder>
```

The output directory must not contain `disk.img`: the probe copies the
input disk there and never attaches the original for writes. The ISO is
read-only. The default machine has 16 MB and a 486 CPU; USB is disabled.
Keyboard typing assumes the same layout as the reference disk.

The probe accepts a line-oriented command language: `run <n>` executes
instructions, `type <text>` and `key <code>` deliver keyboard input,
`shot <name>` writes a PPM screenshot and prints text video memory,
`save <name>` writes a paired snapshot and disk-image checkpoint,
`move <dx> <dy>` and `click left|right` deliver serial mouse input, and
`trace on|off` logs page faults with CR2 and a short instruction history.
Restore a paired snapshot into a new output directory by supplying it as a
fourth argument:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  <path-to-a-saved-.img> <path-to-your-Win95-iso> \
  <new-output-folder> <path-to-a-saved-.snapshot>
```

The probe can also load a full VM configuration folder, copying its hard
disk and floppies before restoring a supplied snapshot:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  --vm <vm-configuration-folder> <output-folder> <path-to-a-saved-.state>
```

`faults.log` records distinct protected-mode `#UD`, `#NP`, `#SS` and `#GP`
locations, registers, cached segments, code bytes and stack bytes; it
excludes virtual-8086 traps and caps its distinct entries to avoid
unbounded growth. Normal Windows demand loading also raises `#NP`, so a
logged exception alone does not mean the guest has crashed. Check the
screenshots before reusing a command sequence with a different disk:
keyboard input depends on the exact prompt shown.

Completing Windows 95 installation, reaching its desktop and validating its
drivers are the milestones this document covers; it does not establish
general Windows 95 compatibility.
