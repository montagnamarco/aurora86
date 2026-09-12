# USB controller and HID input

Set `"usbEnabled": true` in `vm.json` to add a UHCI-compatible PCI controller
at 00:02.0, with a keyboard on root port 1 and a mouse on root port 2.
The setting defaults to false for existing VMs and is retained by Save VM.
Both frontends use the same machine configuration.

The controller's initial I/O BAR is C100h (32 bytes, BAR4). Its command
register gates I/O decoding and bus-master DMA independently. PIRQ routing
uses the PCI bridge and ELCR; firmware/OS configuration must assign an IRQ
and unmask the PIC before interrupts are delivered to the CPU.

The controller advances from emulated time, independently of rendering.
It reads frame lists and queue heads from guest physical memory, executes
SETUP/IN/OUT packets, updates TD status and queue pointers, and handles
NAK, programmed timeout retries, STALL, IOC and short packets. Port registers
expose connection changes, reset, enable and low/full-speed identification.
FRNUM and port writes require words; status change bits use write-one-to-clear.
A finite traversal budget prevents circular guest schedules from hanging the
host. Detailed bus-time accounting, complete single-step/debug continuation,
isochronous traffic and the full suspend/resume timing model remain open.

The HID devices enumerate through endpoint zero, commit SET_ADDRESS and
SET_CONFIGURATION after the status packet, and expose boot/report protocols.
The keyboard has modifier bits, six key slots with rollover, LED output and
idle reports. The mouse has relative motion, three buttons and a wheel in
report protocol. Interrupt-packet retries replay the previous report without
consuming motion twice. The frontend wheel event path still needs wiring.

Frontend set-1 keyboard codes are translated to HID usages. Mouse movement
and button events use the HID device once the guest configures it. Before
USB configuration, input continues through the existing legacy interfaces.
This automatic switch is not a complete UHCI SMI-based BIOS legacy handoff;
transition handling for keys/buttons already held needs further verification.

Snapshots preserve controller timing, ports, pending transfers and HID state,
including held keys, scan-code prefixes and report retries. Attached device
types must match at restore. Enabling USB changes PCI topology, so snapshots
from a VM without USB should be restored with USB disabled.

`UhciTests` submit real DMA structures, including enumeration of a HID device.
`UsbHidTests` cover reports, control transfers, input routing and snapshots.
Windows 98/ME guest enumeration and usability have not yet been verified.
USB storage and simultaneous use of a third device remain in progress.

References:

- [Intel UHCI Design Guide 1.1, archived copy](https://intel-vintage-developer.eu5.org/DESIGN/USB/DESIGNEX/UHCI11D.PDF).
- [USB-IF HID specification](https://www.usb.org/sites/default/files/hid1_12.pdf).
- [USB-IF Bulk-Only Transport specification](https://www.usb.org/sites/default/files/usbmassbulk_10.pdf).
