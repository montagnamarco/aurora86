# Windows Enhanced: MS-DOS Prompt regression

## What works

The full-screen MS-DOS Prompt opens under Windows Enhanced with the
S3 Trio driver, runs MEM, and returns to Program Manager with EXIT. Both the
DOS session and the return to Program Manager render correctly, and the S3
graphics mode is restored after EXIT.

## What the emulator had to get right

Windows leaves and re-enters V86 mode around the DOS session, and exercises
three code paths a DOS-only guest rarely touches:

- **POP must preserve ESP across a restartable fault.** When the stack read
  or destination write of a `POP` raises a page or segment fault, ESP must
  be restored to its value before the instruction, so the page-fault
  handler can map the missing page and retry the same `POP` cleanly.
  ESP-based destinations still use the pointer after the pop. Without this,
  a retried `POP` consumes the stack twice: the second dword is lost, a
  later `RET` reads a stale address, and Windows tears down the virtual
  machine. Four regressions in `ProtectedModeTests` cover 16/32-bit operands
  and destinations within a missing page or spanning its boundary; each
  delivers a real `#PF` through an IDT gate, maps the missing page, returns
  with `IRETD`, and verifies that `POP` consumes exactly one value.
  Restartable page faults are described in the
  [Intel 80386 reference manual, section 9.8.14](https://www.ardent-tool.com/CPU/docs/Intel/386/manuals/prref386/s09_08.htm).
- **Legacy INT 10h AH=00h mode changes must clear VBE state.** Returning to
  mode 03h has to clear the VBE mode and its banking, scanline and
  display-start state, or the text mode displays stale S3 pixels under the
  text palette instead of 720x400 text.
- **The hardware cursor's XOR background must be refreshed before
  recomposing.** The renderer refreshes the background under a stationary
  hardware cursor before composing it on a changed frame, so XOR pixels are
  not inverted a second time when an unrelated row changes.
- **Text-cursor moves and hiding must invalidate the old row** even when no
  video memory is written, or stale caret pixels remain on screen.

`VesaTests`, `S3HardwareCursorTests`, and `TextCursorRenderingTests` cover
these rendering cases, including comparisons against full redraws.

## Known limits

This regression establishes the DOS Prompt round trip at one desktop
resolution and icon layout; other resolutions, PIF settings and Windows
configurations are not covered.

## Reproducing the guest check

Run the reproduction on a temporary copy of your own disk image:

```sh
dotnet run --project tools/WindowsDosProbe -c Release -- <path-to-your-image> <output-folder> 461 210
```

The final arguments give the Prompt icon coordinates for a 1024x768 desktop.
Without them the probe uses (415,130), matching an 800x600 layout. Adjust
them if the group window has moved. The PIF must use full-screen mode: the
probe explicitly checks that a text console opened, so clicking a different
icon cannot produce a false success.

The probe selects the icon, launches it with Enter, runs MEM, and types
EXIT. It saves PPM screenshots, machine snapshots, and an interrupt trace.
Snapshots are diagnostic captures, not substitutes for a matching disk
snapshot. The source image is never modified. A successful run requires the
S3 graphics mode to return after EXIT; inspect the screenshots to confirm
Program Manager and its contents as well.

Before the mode-state fix, opening the Prompt left stale S3 graphics:

![Stale graphics after the DOS mode change](images/windows-dos-before.png)

With the CPU fix, MEM runs inside the Windows DOS session:

![MEM in the full-screen DOS session](images/windows-dos-session.png)

EXIT returns to Program Manager:

![Program Manager after EXIT](images/windows-dos-returned.png)
