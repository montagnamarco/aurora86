# PIIX4 motherboard support

Set `hardware.chipset` to `"i430tx"` in `vm.json` to select the Intel
82439TX host bridge and 82371AB southbridge. The default remains `"i430fx"`.
The programmatic equivalent is `new Machine(chipset: Chipset.I430Tx)`.

The PIIX4 motherboard exposes ISA, compatibility-mode IDE, UHCI USB and
power-management functions at PCI device 1, functions 0 through 3. USB is
present even when the configuration omits an expansion USB card. Its input
controller occupies function 2; the existing second controller for removable
storage remains in an expansion slot. The BIOS routing table and PCI BIOS
describe the integrated USB interrupt on PIRQD.

The IDE function provides a movable 16-byte BMIBA window, initially at
`C000h`. Drivers must enable PCI bus mastering before starting DMA. The
engine accepts physical-region descriptors and ATA READ/WRITE DMA, including
the retry opcodes. Bit 3 of the command register is set for disk-to-memory
transfers: it describes the PCI master's write direction. The disk advertises
multiword DMA and Ultra DMA modes 0 through 2.

Transfers run synchronously. The register interface distinguishes a table
which ends with the transfer, a table which outlasts the transfer, and a table
which runs out early. This implementation does not model bus contention or
hardware transfer latency, so throughput is not a timing model of a PIIX4.

The power-management block uses configuration register PMBA at `40h`,
initially `B001h`. PMREGMISC at `80h`, bit 0, enables its 64-port window;
PCI Command I/O enable is independent. `Machine.RunPost` sets that bit
itself on the i430TX board, the same as a real BIOS would for any of the
chipset's own fixed hardware register blocks. The 24-bit timer runs at 3.579545 MHz
from emulated time, and its overflow status can be acknowledged with a
write-one-to-clear operation. Snapshots preserve its phase and the IDE
engine's pending DMA state, along with PCI configuration.

ACPI 1.0b tables and the SCI/SMI-adjacent runtime (SMI_CMD, GPE0, the power
button, SLP_EN) are covered separately in `docs/ACPI.md` (work package
P4.5). This is still not full host-memory-controller emulation: SMBus, PAM
shadow routing and SMRAM/SMM proper remain unimplemented.
In-flight PIO/ATAPI transfers are outside snapshot coverage. Disk contents
are external to snapshots and must still be paired with the saved state.
Windows 95 OSR2 driver use and throughput require a separate guest
measurement; DOS boot and register-level tests do not establish that result.

Register definitions follow the
[Intel 82371AB PIIX4 datasheet](https://0x04.net/~mwk/doc/intel/82371ab.pdf),
especially sections 5.2 and 7, and the
[Intel 82439TX datasheet](https://www.ardent-tool.com/datasheets/Intel_82439TX.pdf).
