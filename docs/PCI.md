# PCI configuration and firmware

The machine exposes configuration mechanism 1 at CF8h/CFC-CFFh. CF8h is a
DWORD-only latch; byte and word accesses do not change it. Data accesses
use their actual width and byte lane. Disabled cycles and absent functions
read all ones, and writes to them have no effect.

The initial bus is deliberately small:

| BDF | Vendor:device | Class | Function |
| --- | --- | --- | --- |
| 00:00.0 | 8086:122D | 060000 | 82437-style host bridge |
| 00:01.0 | 8086:122E | 060100 | PIIX-style ISA bridge, multifunction |
| 00:01.1 | 8086:1230 | 010100 | IDE, fixed compatibility PIO |

These are minimal configuration models, not complete Intel chipset emulations.
IDE uses the existing primary and secondary controllers, with their legacy
ports and IRQ14/15. Clearing PCI command bit 0 removes both controllers'
command/control ports from the I/O bus; setting it restores them. Native
mode, bus-master DMA, chipset timing registers are not advertised. IDE uses its legacy IRQs;
the bridges themselves have no PCI interrupt pins. The bridges have no relocatable resources. `PciFunction`
provides masked 32-bit I/O and memory BARs for future devices; size probes
and aligned addresses are tested on a separate fixture. A device owner must
connect configuration changes to actual address decoding.

INT 1Ah B101h reports BIOS 2.1 and mechanism 1. B102h/B103h find functions
by identity/class and zero-based occurrence. B108h-B10Dh read/write the same
configuration space as the ports without changing CF8h. Invalid register
alignment/range returns AH=87h; absent functions still return all ones with
success. Special cycles remain unsupported (AH=81h with carry set).
B10Eh returns the slot routing options, including required buffer size
(AH=89h if too small). B10Fh programs the route and ELCR, returning AH=88h
for invalid slots/pins/IRQs; the caller updates interrupt-line registers
and PIC masks. Both 16-bit and 32-bit routing-buffer pointers are supported.

A paragraph-aligned, checksummed `_32_` directory lives at F000:1A60. Its
32-bit far entry at F000:1A70 resolves `$PCI` to the service at F000:1A80.
The directory reports errors in AL and preserves flags; PCI services report
AH/carry. Tests execute real INT instructions and protected-mode 32-bit far
calls through the ROM hooks and their actual return instructions.

Snapshots append the configuration latch and all functions. Loading an older
snapshot without this extension restores PCI power-on defaults, including
IDE decoding. POST also restores those defaults.

The ISA bridge implements PIRQ routing registers 60h-63h, initially disabled
(80h), with writable mask 8Fh. Four virtual slots at bus 0, devices 2-5,
rotate INTA-D across the four links. The `$PIR` table at F000:1AA0 describes
this same wiring and IRQ bitmap DEF8h; its checksum covers all 96 bytes.
Device owners call `Pci.InterruptRouter.SetInterrupt` to assert or release
their pins. Sources sharing links or destination IRQs are ORed before
reaching the PIC, separately from legacy inputs.

ELCR at 4D0h/4D1h selects level triggering with masks F8h/DEh. A held level
is eligible again after EOI, until released. The PIC now resolves fixed
priority in the correct order and derives the cascade from serviceable
slave requests, so masked slave requests do not block master devices.
Snapshots include ELCR, held inputs and PCI source assertions; older states
without these extensions retain edge-triggered defaults.

Windows 95 now enumerates the PCI bridge and IDE function; the guest checks
and remaining IDE limitation are recorded below. S3 PCI integration remains open.

Sources:

- [PCI-SIG PCI BIOS Specification 2.1, archived in a USPTO exhibit](https://ptacts.uspto.gov/ptacts/public-informations/petitions/1476836/download-documents?artifactId=27J5CrjiE24Df0KzGjs997WQDtKsNytR9HpsAAzj2Su4a9YTBU8i9L8): firmware entry points, calling conventions and status codes.
- [Intel E8501 datasheet](https://www.intel.com/content/dam/doc/datasheet/e8501-chipset-north-bridge-datasheet.pdf): mechanism 1 address latch and access widths.
- [Linux PCI identifiers](https://github.com/torvalds/linux/blob/master/include/linux/pci_ids.h): Intel 82437 and 82371FB identifiers.

- [Intel PIIX/PIIX3 datasheet](https://bitsavers.trailing-edge.com/components/intel/pci/290550-001_82371FB_82371SB_PCI_ISA_IDE_Xcelerator_199605.pdf): PIRQ route registers and ELCR.
- [Intel 8259A datasheet](https://pdos.csail.mit.edu/6.828/2010/readings/hardware/8259A.pdf): fixed priority and level-triggered requests.
- [Coreboot PIR structure](https://chromium.googlesource.com/chromiumos/third_party/coreboot/+/edf528bfea0fcf768436e8e05a1397ce902bc892%5E/src/arch/x86/include/arch/pirq_routing.h): table header layout.

## PnP BIOS bus discovery

The PnP BIOS now announces the PCI bus as `PNP0A03`, appended after the
existing system nodes so their handles do not change. The exported Windows
95 registry previously contained the ISA bus but no PCI enumeration tree,
even though the machine already implemented PCI configuration access.
A firmware-call regression reproduces the missing node and now passes;
30 PnP/PCI tests pass. On the disposable Windows 95 installation, the next boot now detects the
Intel 82371FB PCI-to-ISA bridge and requests a restart to finish installing
the new hardware. After that restart Windows also detects the Intel
82371FB PCI Bus Master IDE Controller and finds its driver in the local INF
directory. IDE and CD access remain under verification.

The node uses the system/peripheral-bus type and cannot-disable/cannot-configure
attributes used by the [DOSBox-X PCI system node](https://dosbox-x.com/doxygen/html/bios_8cpp_source.html).
It has no allocated I/O resources, matching that reference implementation.

## PIIX IDE channel enable registers

After installing the PCI IDE driver, the disposable Windows 95 guest reported
Device Manager code 29 (disabled in hardware). Configuration words `40h` and
`42h` read zero, although both IDE channels decoded their compatibility ports.
The PIIX IDETIM bit 15 enables the corresponding channel. Both words now start
at `8000h`, representing firmware setup, and independently gate their channel
together with PCI command bit 0. The writable mask is `B3FFh`; timing fields
are retained but do not yet alter transfer timing. Bus mastering remains absent.
The register definition follows section 2.3.10 of the Intel PIIX/PIIX3 datasheet
linked above. After rebooting the disposable guest, code 29 is gone. Windows
instead reports a resource conflict (code 15); the two BIOS IDE nodes also
claim the compatibility ports and IRQs. CD access remains unresolved.

New snapshots distinguish implemented IDETIM registers from the zeros in old
snapshots. Loading an old snapshot enables both timing registers while retaining
the saved PCI command; a new snapshot preserves intentionally disabled channels.

The BIOS no longer announces the same channels as separate `PNP0600` devices.
PCI enumeration owns their resources. BIOS handles `0Ah` and `0Bh` are retired;
the enumeration function follows stored handles rather than list indices so
the video, memory and PCI-bus nodes keep their previous identities. Two
regressions failed before the change, and all 35 PnP/PCI tests pass. The guest
installed the primary and secondary dual-FIFO channels across two requested
restarts. The PCI controller no longer has a warning or code 15. The primary
channel now reports code 10, while the secondary channel has no warning;
the CD remains absent. This is a separate initialization failure still under
investigation, not a completed CD fix.

## BIOS reads and the Windows 95 disk monitor

The code-10 trace showed a successful ATA IDENTIFY and sector-zero read,
followed by an INT 13h read under the driver's I/O monitor. At
`0028:C10486B7` the driver tested its observed-I/O flag; it remained clear,
and the channel was rejected at `C10486CD`. The native BIOS handler had
read the disk image without executing any port instructions. DOSBox-X
also documents this detection requirement in its
[IDE BIOS integration](https://dosbox-x.com/doxygen/html/ide_8cpp_source.html).

Validated CHS reads for BIOS disks 80h and 81h now enter a ROM PIO routine.
The native helper translates the address and prepares registers; the guest
executes the command, status polling and INSW transfer. PUSHAD and the
original interrupt frame preserve the caller's registers and flags. The
routine masks device interrupts with nIEN while polling, acknowledges
completion and restores the previous device control. Unsupported functions
and requests outside this path retain their existing handlers; extended
INT 13h reads and writes have not yet been moved into the ROM routine.

Four regressions failed before this change. Both cables now pass data and
register-preservation checks, and nIEN suppresses IRQ delivery while retaining
an unacknowledged request until re-enabled. All 31 targeted tests pass. The
CHS translation test needed more than its former 40-instruction allowance;
all nine disk-extension tests pass with the actual PIO transfer completed.
On the disposable guest restored immediately before driver initialization,
Windows now performs native IDE disk I/O, identifies the ATAPI slave and
shows D: with the ISO's files in Explorer. Copy/restart/media-change checks
remain separate acceptance steps.

`Win95SetupProbe` accepts `AURORA_IDE_LOG=1` for bounded task-file logging.
Adding `AURORA_IDE_TRACE=1` records the first primary ATA read until the next
drive selection and emits only instructions in that driver's 64 KiB code
region. The larger instruction ring is allocated only for this diagnostic.

The first file-transfer check succeeded: Windows copied `D:\README.TXT` to
`C:\CDTEST.TXT` through `COMMAND.COM /C COPY`. Extracting the resulting file
from the disposable disk gave 3728 bytes and the same SHA-256 as the ISO:
`43b018a2ace818fb9fabb2bb41311de1d9670edfffda8295f538e8eba73258e2`.
The DOS screen transition needs a separate video check; this copy verifies
storage, not completion of the DOS-window/display acceptance tests.

A cold boot after interrupting the DOS-copy run required ScanDisk repairs
and subsequently reported a registry/configuration error. That acceptance
check remains open. In a separate run from the intact pre-driver checkpoint,
Windows reached the desktop, performed Start > Shut Down, and cold-booted
back to login without ScanDisk or a registry error. The latter did not repeat
the DOS-copy/display transition, so it does not isolate the cause of the
interrupted run's corruption.
