# Diagnosing the Windows 95 SETUPX fault

The Windows 95 OSR2.5 CD named `Win95_OSR25.iso`, booted through the reference
DOS 6.22 disk with HIMEM and EMM386, reproduced the application error:

```text
SUWIN caused a General Protection Fault in
module SETUPX.DLL at 0002:2134.
```

This happened after Continue, while Mini-Windows prepared the setup wizard.
The failing instruction was `CA 08 00` (`RETF 8`). The trace recorded:

```text
CS:IP       0D87:2134     CS base 0017EE20
SS:SP       0E57:B4FC     SS base 001A9A90
return      0DEF:1EF4
CR0         80000013
CR3         00373000
LDT base    80009000
LDT[0DEF]   97 0D C0 C8 15 7B 10 01
```

The return descriptor is executable but not present (access byte `7B`). Its
placeholder limit, `0D97`, is below the return offset, `1EF4`. The old far-return
implementation checked that limit before the descriptor's type and presence,
and raised general protection with error code zero. Windows needed the
segment-not-present exception to reload the discarded segment.

The fix validates the return code descriptor's type, privilege and presence
before checking its limit. These checks do not change CS, and a fault restores
the original stack pointer so that the instruction can be retried. This is the
order specified for same-level returns in the
[Intel 80386 RET reference](https://www.ardent-tool.com/CPU/docs/Intel/386/manuals/prref386/RET.htm).

`ProtectedModeTests.FarReturnValidatesTheDescriptorBeforeItsLimit` exercises
16-bit and 32-bit returns with an absent code descriptor, a wrong descriptor
type, and a present code segment with an insufficient limit. It checks both
the exception and the preserved return frame. Four of the six cases failed
before the fix; all six pass with it. The protected-mode reference suite also
passes all 6,100 vectors.

A fresh boot with the corrected CPU reached the Software License Agreement,
using the same source disk, ISO and keyboard sequence. At the original failing
instruction the trace now records `#NP` for selector `0DEF`, with the same
registers and return frame. Mini-Windows reloads the segment and continues.
This verifies the specific `0002:2134` blocker; the license was not accepted
and the rest of installation was not exercised in this run.

## Reproduction harness

Build and launch the interactive probe:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  images/hdd.img /path/to/Win95_OSR25.iso /tmp/win95-probe
```

The output directory must not contain `disk.img`. The probe copies the input
disk there and never attaches the original for writes. The ISO is read-only.
The default machine has 16 MB and a 486 CPU; USB is disabled. Keyboard typing
assumes the Italian layout loaded by the reference disk.

For the measured reference image, the following commands reach the failure
with the old CPU code. Instruction counts are wait budgets, not timing claims:

```text
run 400000000
save dos-prompt
type d:
type cd win95
type setup
run 300000000
key 1c
run 400000000
key 01
run 400000000
shot welcome
save welcome
key 1c
run 950000000
shot after-continue
save after-continue
```

The first Enter starts the preliminary check. Escape accepts running from DOS
when Setup recommends launching from the existing Windows installation.
The final Enter selects Continue in Mini-Windows. Check the screenshots before
using this sequence with a different disk: keyboard input depends on the prompt.

`shot` writes a PPM image and prints text video memory. `save` writes a snapshot
and a matching image copy while the guest is stopped. Restore the pair into a
new output directory by supplying the snapshot as the fourth argument:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  /tmp/win95-probe/welcome.img /path/to/Win95_OSR25.iso \
  /tmp/win95-retry /tmp/win95-probe/welcome.snapshot
```

`faults.log` records distinct protected-mode #UD, #NP, #SS and #GP locations,
registers, cached segments, code bytes and stack bytes. Normal Windows demand
loading also raises #NP; the existence of a logged exception alone does not
mean that the guest has crashed. The log excludes virtual-8086 traps and caps
its distinct entries to avoid unbounded growth. Its instruction count identifies
a run batch, not the exact instruction within it.

Completing Windows 95 installation, reaching its desktop and validating its
drivers are separate milestones; correcting this setup fault does not establish
those results.

## Stack fault during hardware detection

The user's `20260910-161446.state` checkpoint reproduces a second blocker by
pressing Next and waiting. It reports a stack fault in `SYSDETMG.DLL` at
`0001:00EB`, while detecting IDE/ESDI hardware. The instruction is `RETF 4`.
The inner stack at the fault contains these 16-bit words:

```text
SS:SP = 0068:309C
4309 1A27   ; outer return IP and CS
0170 0000   ; four bytes of copied arguments, including the IDE port
AF80 0E57   ; outer SP and SS
```

The old implementation popped `0170` as SP and `0000` as SS. It had omitted
the parameter copy placed on the inner stack by a call gate. An interlevel
`RETF n` must skip that copy before loading the saved outer stack, then remove
`n` bytes from the outer stack as well. The fix checks the frame extent and
skips the copied parameters. A rejected return also restores the original
CS, SS and privilege, so fault delivery describes the instruction that failed.

`FarReturnDiscardsCopiedParametersBeforeRestoringTheOuterStack` covers both
operand sizes; both cases failed before the fix. Another regression verifies
that an invalid outer stack does not leave CS or privilege partially changed.
The ten targeted far-return cases and all 6,100 protected-mode vectors pass.

The probe can now load a VM configuration, copying its hard disk and floppies
before restoring the supplied snapshot:

```sh
dotnet run --project tools/Win95SetupProbe -c Release -- \
  --vm images/win95 /tmp/win95-hardware-probe \
  images/win95/snapshot/20260910-161446.state
```

```text
save before-detection
key 1c
run 300000000
shot detection
```

This preserves the VM's RAM, CPU, network and media configuration. A GUI state
file does not itself preserve disk contents: keep a matching disk copy for
repeatable experiments. The probe's `save` command creates the hard-disk copy
with its checkpoint; its floppy copies remain in the output directory.

With the corrected CPU, the supplied checkpoint advances through 28%, 53%
and 97%, and `SETUPLOG.TXT` records `Detection=Passed`. The full non-slow suite
passes 1,343 tests, the protected vectors pass 6,100 tests, and the complete
solution builds successfully.

The cold-restored run then displays SU-0014. Its setup log reports failure
accessing `D:\*.*`; Continue does not establish successful installation.
This is separate from the corrected stack fault. The current snapshot format
does not preserve `GuestHasItsOwnCompactDiscExtensions`; a fresh machine can
therefore reattach the emulator's CD redirector to a guest already using
MSCDEX. That restore path needs investigation before blaming ATAPI detection.
The original disk and state file were not modified by these runs.


## VPOWERD protection error during startup

A cold boot of the installed Windows 95 disk reproduced:

```text
While initializing device VPOWERD:
Windows protection error. You need to restart your computer.
```

The fault was #GP(0029h) at `00A8:1A53`, the APM BIOS entry's RETF.
Its 32-bit code segment had base `000F0000`; SS was flat and ESP was
`C13A1E60`. The stack contained return address `C02B5497` and selector
`00000029`. AX was `8612`: function 5312h had returned "not supported".

`ApmBios` ignored `fromEntryPoint` and wrote carry into the word at ESP+4,
as though every call had an INT 15h frame. For a 32-bit far call, that word
is the return selector: reporting failure changed 0028h to 0029h. For a
16-bit far call, it modified the caller's stack data instead. Neither form
returned the requested carry flag in the live flags.

The handler now computes status in the live carry flag and copies it into
stacked FLAGS only for the interrupt entry. Four tests execute real 16-bit
and 32-bit far calls, cover success and unsupported-function failure, and
check carry, return selector, stack pointer and a caller stack sentinel.
All four failed before the fix. Afterward all 18 system-service tests and
all 1,347 non-slow tests pass; the whole solution builds successfully.

On a separate disk copy the same installed guest reaches the Windows login
prompt and then the hardware configuration dialog. This does not yet prove
completion of that configuration or a usable desktop. The reported corrupt
clouds logo remains unverified: the recovery boot used here did not display
it. Checkpoints in `/tmp/win95-vpowerd-fixed` preserve the boot menu and the
login prompt with their matching disk images. The screenshot writer now
exports the framebuffer's actual RGBA byte order, avoiding swapped red and
blue in diagnostic PPM files.


## Plug and Play stalls after login

The installed guest next stopped at "Setting up Hardware". An initial CPU
profile showed INF parsing, and `SETUPLOG.TXT` initially ended at
`Build Driver Index File / Start`. Continuing advanced past indexing and
reached `Enum\ISAPNP\CTL0044\00000001`, the Sound Blaster card.

The persistent loop was at `0028:C102C3E0`:

```text
in al, dx
or al, al
js reply_ready
loop read_status
```

DX was 022Eh and the DSP always returned 7Fh (no data). The driver's timeout
loaded only CX but decremented ECX, whose upper half was nonzero, extending
the wait to billions of instructions. The preceding command stream was
`05 00 00 03 04 FC 04 FA 0F 83`: the final operation reads ASP register 83h.
The emulator neither framed these ASP commands nor produced that reply.

The DSP now consumes the arguments of ASP mode/codec/register writes and
answers register probes using read-only compatibility values. Command 08h,
subcommand 03h reports FFh (no optional ASP chip). This follows the
[reference implementation and hardware observations in DOSBox-X](https://dosbox-x.com/doxygen/html/sblaster_8cpp_source.html).
It does not implement an ASP audio processor or microcode execution.

Five new port-level tests failed before the change and pass afterward:
the measured Windows command sequence, argument framing for three write
commands, and the absent-chip version response. All 20 DSP tests pass. The full non-slow suite passes 1,352 tests, and
the whole solution builds successfully.
Replaying the paired pre-stall checkpoint now records `COMPLETE` for
CTL0044, proceeds to RTL8019 and asks for "Windows 95 Disk18" to copy network
files. Media selection and completion of this setup stage remain separate
from the corrected DSP polling stall. Original user images were not modified.


## Follow-up startup failures still under investigation

A later cold boot of the user's installed disk reproduced random text and
attributes before ScanDisk. `/tmp/win95-spool32/before-scandisk.snapshot`
and its matching `.img` preserve that state. This updates the earlier logo
limitation: visible corruption is now reproduced, but its cause is not yet
established.

The subsequent reported `spool32.exe` error and unresponsive Close button
have not yet been reproduced. The copy instead displayed a `regsvr32.exe`
error after network driver file installation was cancelled on the copy.
Its paired checkpoint is `/tmp/win95-spool32/regsvr-error.snapshot`.
A snapshot of the user's exact failure has been requested.

The diagnostic probe now accepts `move <dx> <dy>` and `click left|right`.
As of September 12, 2026, new machine snapshots preserve the serial mouse
UART registers, queued receive bytes, pending serial movement, and both
current and last-reported button states. Tests restore these into a fresh
machine and verify the remaining packet bytes, IRQ state, and subsequent
reports. Older snapshots remain readable but cannot reconstruct serial-port
state they never captured; restoring those files therefore cannot validate
live mouse input. This change does not establish a new guest or spooler
result.


## REGSVR32: a retried PUSH corrupts the return stack

The supplied `20260910-230320.state` shows **Regsvr32**, rather than Spool32.
The error details remain in guest memory even with the dialog collapsed:
`REGSVR32 caused an invalid page fault ... at 0000:80000040`, with
CS=0137h, EIP=80000040h and ESP=0055F010h. Advancing the restored guest also
removes the visible dialog without input, so the button symptom alone does
not establish a mouse transport failure.

A replay from the paired login checkpoint captured the cause. At
`0137:BFF79724`, `PUSH dword ptr [ESP+0Ch]` starts with ESP=0055F000h.
The missing stack page faults at 0055EFFCh, but Push32 has already lowered
ESP. Windows maps the page and retries; a second write-protection fault
lowers ESP again to 0055EFF8h. After the retry succeeds, the stack is eight
bytes below its intended position. The subsequent RET reads an argument,
80000040h, as its return address.

Push16 and Push32 now calculate the destination locally, write the value,
and only then commit SP or ESP. The existing 16-bit stack wrap convention
is preserved. Four tests execute PUSH, service its page fault, return with
IRETD and retry, checking the original stack pointer and the pushed value
for both operand widths and both stack widths. All four failed before the
change and pass afterward. All 6,100 protected-mode reference vectors pass,
and the whole solution builds successfully.

With the fix, `/tmp/win95-regsvr-trace/before-regsvr.snapshot` and its paired
disk advance past the failing registration and reach Windows 95's final
configuration of Control Panel and Start menu entries. The resulting paired
checkpoint is `/tmp/win95-push-fixed/setup-items.snapshot`. The user disk was
not modified. A snapshot already taken after stack corruption cannot undo
that corruption; verification must restart from before the faulty PUSH.
The probe now offers `trace on|off` and logs page faults with CR2 and a short
instruction history when tracing is enabled.

The full non-slow suite also passes 1,356 tests. Continued guest execution
reaches the time-zone selection dialog without the REGSVR32 error.

Skipping optional Messaging and printer setup on the disposable copy then
reaches "Setup has finished configuring your system" and the restart prompt.
The paired checkpoint is `/tmp/win95-push-fixed/setup-complete.snapshot`.

After the requested guest restart, the serial mouse is initialized again.
Sending movement to the login dialog's OK button and a left-button press
and release successfully completes login. No serial-button protocol change
was required. This verifies the guest input path after boot. The earlier
snapshot omission of UART state is covered by the serial snapshot change
documented above.

The restarted guest subsequently reaches the desktop with icons and taskbar.
`/tmp/win95-push-fixed/desktop-verified.snapshot` and its paired image retain
that result. The optional network file copy was cancelled earlier on this
disposable disk; this is not validation of networking or every installed
component. The original user disk remains untouched.

## DOS text services and return to Windows 95

The BIOS now completes AH=00 text-mode changes through guest-visible ROM
instructions when the display is virtualized. AH=00 respects the requested
text mode and updates the guest BDA, text page, palette, cursor, and metadata
without writing the physical display in the intercepted path. The other
observed text services, AH=02, AH=03, AH=08, AH=0E, and AH=0F, now use the
guest BDA, paging, and VGA ports as the guest sees them. Mode 07 and other
BIOS functions retain their existing paths.

The final Windows 95 run reached the desktop after `EXIT`, including the
Start bar and CD contents. The fresh restore remained in mode 0101 for the
entire run, with no native handler override or alternate Enter path; the
trace records 18 ATAPI packet commands and the termination path through
INT 21h AH=4Ch in both VM86 and protected mode. Earlier startup input was
intermittently ignored, but the final run settled without a workaround, so
this does not establish a keyboard defect or its absence.

An earlier diagnostic run rendered only part of the DOS text. The final
normal and restored runs rendered `ver`, `dir d:`, the copy command, and
`EXIT` successfully before returning to the desktop. This does not claim
complete DOS text compatibility, a new ATAPI fix, or a result for the
separate user-reported black-screen case.

The focused BIOS text tests pass 71/71, the final non-slow suite passes
1,486/1,486, and the Release solution build completes with zero errors and
zero warnings.

## CD ownership across snapshots (11 September)

A cold machine with a mounted ISO registers the emulated CD filesystem.
Previously, restoring a snapshot taken after a native CD driver had taken
ownership left that mount active and reset the ownership flag. Reattaching
the redirector then marked the restored guest CDS; the next ATAPI command
could remove that mount and clear flags belonging to the guest driver.

New snapshots retain CD ownership. The host mount is reconciled before RAM
is restored, preserving the guest CDS bytes. Tests cover in-memory and
file round trips, returning to the emulated CD services and loading the
previous format. The two ownership regressions failed before the fix;
59 CD, IDE, snapshot and PCI tests pass afterwards. Old snapshots remain
readable but cannot supply ownership information they never recorded.
This does not yet establish the cause of the user's missing CD after boot.

On September 12, 2026, a fresh boot with the current Windows 95
configuration shows `Win95_OSR25` on `D:` in My Computer and lists 11 items
at the CD root. A 3,728-byte
`README.TXT` copied before the snapshot matches the ISO byte-for-byte with
SHA-256 `43b018a2ace818fb9fabb2bb41311de1d9670edfffda8295f538e8eba73258e2`.
After restoring the paired snapshot into a fresh machine, a distinct
3,664-byte `SETUP.EXE` copied from the CD to `CDAFTER.EXE` also matches the
ISO with SHA-256
`8028cacc62cf84ebc47546e0784f363b09f54485c5ae97b2e71816f84c9a46a8`.
The trace records an ATAPI packet at port `01F7` from `0028:C101B4B9`.
An earlier diagnostic run remained in text mode after `EXIT` and rendered
only part of the DOS text. The final normal and restored runs returned to the
desktop; the graphical directory listing was verified after restore. These
results verify CD reads and copies across restore without claiming a new
ATAPI fix or complete guest compatibility.

## Empty diskette drive type (11 September)

The current user configuration has no floppy image inserted. CMOS reports
A: as a 1.44 MB drive, but INT 13h functions 08h and 15h previously treated
the missing image as an absent drive. The copied Windows 95 installation
showed "5¼ Floppy (A:)" in My Computer.

Disk services now retain diskette geometry independently of media. Attaching
an image selects the drive geometry as before; ejecting it preserves that
description. CMOS and the BIOS equipment count follow the installed drives.
Two INT 13h regressions failed before the change and pass after it; 14
related disk tests pass. A fresh boot of the disposable Windows 95 disk now
shows "3½ Floppy (A:)". The screenshot is
`/tmp/win95-floppy-20260911/drive-type.png`.

This does not implement an FDC or validate every diskette format. The BIOS
reference path is SeaBIOS `disk_1308`, which reports installed drive geometry
without reading the medium:
https://github.com/coreboot/seabios/blob/master/src/disk.c

## Wallpaper reproduction (11 September)

The disposable VM in `/tmp/win95-after-ordered-shutdown-20260911` boots
after an orderly shutdown without ScanDisk or a registry error. Its active
adapter is Standard Display Adapter (VGA), with `vga.drv,*vdd`, version 4.0,
at 640 by 480 in 16 colors. The S3 description in `SYSTEM.INI` is stale;
this experiment does not validate the S3 driver.

The paired `before-wallpaper.snapshot` and `.img` precede the wallpaper
selection. Applying Carved Stone in Tile mode, switching property tabs,
and removing the wallpaper all work. Clouds instead produces a blue
preview and a blue desktop with small repeating artifacts. Apply returns
and the dialog can be closed; a complete guest freeze is not established.
The paired `clouds-corrupt` checkpoint preserves this second case.

The source `WINDOWS\CLOUDS.BMP` extracts as a valid cloud image. Copying it
to the root directory through Explorer's Copy and Paste commands produces
307,514 identical bytes, with SHA-256
`b3a198b53a09aa89bb85be8309af245b203046b8a3947180822b52303cf0b5e2`.
This checks a larger file transfer through the native IDE driver without
entering DOS. It does not identify the faulty graphics operation or rule
out other storage defects. `bitmap-copied.png` records the resulting file.
