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

## EHCI (USB 2.0 high speed)

`EhciController` (`Devices/Usb/EhciController.cs`, plus the `.Ports.cs` and
`.Schedule.cs` partials) models Intel ICH9's EHCI 1.0 host controller: PCI
8086:293A, class 0C0320 (serial bus controller / USB / EHCI programming
interface), SBRN 20h. Every device it ever attaches directly is USB 2.0 high
speed (480 Mbit/s); low- and full-speed devices stay on the companion UHCI
controller this device always ships next to on real ICH silicon (ICH9's
device 3, function 7, alongside the "storage" UHCI at function 0 -- wiring
the two together, the multifunction header and port routing between them is
`UhciCard`'s job, not this controller's). The register file itself (BAR0)
is reached through the ordinary CPU-path memory bus, exactly like any other
memory-mapped device; everything the schedule reads or writes -- queue
heads, qTDs, payload bytes -- is a genuine bus-master access and goes
through `PciBus.DmaMemory` (`IDmaMemory`) instead, exactly as
`AhciController`'s own command engine does: that path does not apply A20
masking (a bus master never sees the CPU's own address-wrap quirk) and
raises `Written` for anything it changes in RAM.

BAR0 is a 4 KB memory window, wide enough to reach PORTSC at op+44h/48h with
plenty of reserved space left over (owner decision, matching QEMU's
`ehci-pci`; the plan's original 1 KB would not have worked at all --
`PhysicalMemoryBus.TryMapDevice` only ever claims whole 4 KB pages, so a
legal 1 KB placement such as FEBFFC00h would have decoded nothing). CAPLENGTH
20h places the operational registers at BAR0+20h, HCIVERSION reads 0100h.
HCSPARAMS reports N_PORTS=2, N_PCC=2, N_CC=1 (one companion, this device's
own UHCI); HCCPARAMS reports IST=1 and EECP=0 -- there is no USBLEGSUP
capability, so there is no BIOS/OS handoff to emulate, which is legal per the
specification when a platform never needs one. Every offset past the
operational block reads 0, not FFh: it is still inside the mapped page, just
reserved.

USBCMD/USBSTS/USBINTR, FRINDEX and the async/periodic list base registers
follow EHCI 1.0 chapter 2 exactly: USBSTS bits 0-5 are write-one-to-clear,
HCHalted (bit 12) mirrors RS, and the Asynchronous/Periodic Schedule Status
bits mirror ASE/PSE while running (bandwidth reclamation itself, bit 13, is
not modelled and always reads 0). FRINDEX advances one microframe (125 us)
per `Advance` call regardless of PCI bus mastering -- it is a timer, not a
DMA operation -- while the async and periodic schedules only walk guest
memory when `PciFunction.BusMasterEnabled` is true; ASYNCLISTADDR/
PERIODICLISTBASE of 0 is a legal physical address like any other; only ASE/
PSE decide whether either schedule runs. Frame List Rollover (USBSTS bit 3)
follows FRINDEX bit 13 toggling, per section 2.3.2 -- with the fixed
1024-entry periodic list that happens twice per 16384-microframe period, at
the wrap to 0 and again halfway through it, not only at the wrap. The
Interrupt Threshold Control field (USBCMD bits 23:16) is stored and
guest-writable but has no scheduling effect: every guest command still runs
to completion synchronously within the microframe that reaches it, exactly
as `UhciController` and `AhciController` already do, so there is nothing left
"in progress" for a coalescing window to batch.

CONFIGFLAG and PORTSC implement the routing handshake plan decision 3
describes: CONFIGFLAG=0 forces every port's PortOwner bit to read 1
(companion-owned) regardless of its stored value; setting CONFIGFLAG=1
reclaims every port for EHCI (PortOwner forced to 0), and either transition
-- including CONFIGFLAG reverting to 0 as part of HCRESET -- fires
`PortRoutingChanged` for each port whose effective owner actually changed. A
plain register reset does not by itself claim a port's connection changed:
CCS still reflects whatever is physically attached, but CSC/PEC stay clear
unless the reset also moved a port between EHCI and the companion. PE itself
is read-only from software's side except for clearing it: a write only ever
disables an enabled port when it sets PE's own bit to 0; leaving PE=1 in an
ordinary read-modify-write (as a driver touching FPR or Suspend naturally
would) has no effect, per section 2.3.9. While a port is held in reset, PE
reads 0; when software releases PORTSC.PR, a device that answered
`IUsbDevice.HighSpeedCapable` stays enabled (PE=1) and is told
`SelectSpeed(true)`, while any other port (or an empty one) leaves PE clear,
which is how a driver recognises a full-/low-speed device and hands the port
to the companion by writing PortOwner=1. PORTSC's Line Status field reads
the K-state (01b) for a low-speed device the instant it is attached, letting
a driver release such a port to the companion without ever asserting reset
on it, exactly as the specification describes. PP always reads 1: HCSPARAMS'
PPC bit is 0, so this emulation never models a port that can be powered
down. `EhciController.Attach(port, device)` is the physical-presence half of
this handshake -- the card that owns port routing calls it only while a port
is actually routed to EHCI, mirroring how `UhciController.Attach` already
works for its own two ports.

The async schedule (control and bulk transfers) and the periodic schedule
(interrupt transfers, one queue head per scheduled microframe via its
Interrupt Schedule Mask) share one engine that walks queue heads (QH) and
queue element transfer descriptors (qTD), EHCI 1.0 chapter 3: a QH's
"overlay" (its Next/Alternate-Next qTD pointers, Token and five buffer
pointers) is the one live copy of whichever qTD is currently executing,
loaded byte-for-byte from the standalone qTD in guest memory it was fetched
from, and every status change -- Active clearing, Halted, Babble, XactErr,
Total Bytes, Current Page and the buffer offset -- is written back to that
same standalone qTD as well as the overlay (section 4.10.5): a driver
(Linux's `qh_completions`) reads completion status from the qTD's own memory
directly, not only from the queue head. A queue head whose overlay is
Halted and not Active -- Linux's own permanent async head is built exactly
this way, with Maximum Packet Length 0 -- is skipped, never faulted; a
freshly fetched qTD that is not itself Active yet is loaded for visibility
and the pass stops there rather than looking further ahead, which is what
keeps a qTD that links back to itself, or to a peer that does the same, from
ever being walked more than once. An Active qTD's queue head keeps chaining
depth-first through further qTDs, exactly as `UhciController.ExecuteFrame`
walks one TD queue within a frame, for as long as each one completes without
stalling -- so a driver's whole SETUP/DATA/STATUS chain for one control
transfer, or a multi-packet bulk qTD, can retire within a single microframe
when nothing NAKs. A short IN packet ends its qTD and the Alternate Next qTD
Pointer is followed instead of Next, per section 4.10.2. Every loop here is
bounded: the async and periodic list walks against a malformed or circular
list of queue heads, one queue head's own qTD chaining against a qTD chain
that cycles back on itself, and one qTD's own multi-packet transfer against
an endpoint whose Maximum Packet Length can never make the progress its qTD
asks for (an OUT or IN qTD with pending bytes and a Maximum Packet Length of
0 simply never runs -- exactly as a permanently NAKing device would leave
it -- rather than spinning). There are no isochronous or split-transaction
descriptors: every attached device is high speed (plan decision 5), so an
iTD, siTD or FSTN link pointer anywhere a queue head was expected -- or an
out-of-range descriptor address -- is a host system error (HSE) that halts
the controller, exactly like `UhciController.Fatal`.

Transaction outcomes follow `IUsbDevice.Transfer`'s handshake exactly as
`UhciController` already interprets it: a NAK leaves the qTD Active and
untouched; a STALL clears Active and sets Halted; a Timeout decrements the
qTD's own CERR field (a zero CERR means unlimited retries, per section 3.5)
and sets XactErr on every one of those retries, not only the last, adding
Halted once CERR reaches zero (section 4.10); an IN whose reported length
exceeds the packet this qTD's queue head offered (its Maximum Packet Length
field, clamped rather than faulted past this controller's own 1024-byte
ceiling -- a high-speed interrupt or isochronous endpoint's legal maximum,
USB 2.0 section 5.7.3/5.6.3, wider than a bulk endpoint's 512) is babble,
setting Halted+Babble. IOC asserts USBINT (bit 0) whenever a qTD that
requested it retires, successfully or not; a short packet asserts USBINT
too, independently of IOC (section 2.3.2); USBERRINT (bit 1) is also
asserted for every error retirement, matching `UhciController`'s own
dual-bit behaviour. USBINT is asserted at the PCI line exactly when
`(USBSTS & USBINTR) != 0`; the IAAD doorbell (USBCMD bit 6) is honoured
synchronously at the register write that rings it and sets IAA (USBSTS bit
5) immediately, since -- as above -- there is nothing left running in the
background for it to wait on.

Data Toggle Control (Endpoint Characteristics bit 14, not the unrelated
Control Endpoint Flag at bit 27) decides whether a freshly fetched qTD's own
Data Toggle bit is trusted (DTC=1, taken as-is when the overlay is loaded)
or the overlay's already self-maintained one is kept instead (DTC=0,
sections 3.6 and 4.10.2). Either way, once a qTD is executing, every
successful data packet still flips the overlay's toggle and writes it back,
so a DTC=0 queue head's own toggle keeps advancing correctly across many
qTDs even though the driver never has to compute it itself and can leave
every qTD's own dt bit at a fixed placeholder -- exactly what Linux's
`ehci-hcd` does for its own bulk and interrupt queue heads.

`UsbMassStorageDevice` reports `HighSpeedCapable => true`: its device
descriptor's bcdUSB always reads 0200h and it always answers the
DEVICE_QUALIFIER (type 6) and OTHER_SPEED_CONFIGURATION (type 7) descriptors
USB 2.0 section 9.6.2/9.6.4 require of a high-speed-capable device --
including through UHCI, at full speed, since a real USB 2.0 stick answers
exactly the same way when it ends up on a full-speed-only port (owner
decision). Its bulk endpoints report 512-byte packets once `SelectSpeed(true)`
tells it an EHCI port is in use and 64 bytes otherwise (control endpoint 0 is
fixed at 64 bytes regardless, per USB 2.0 section 5.5.3); everything else
about full-speed transport behaviour through UHCI is unchanged.

Not modelled: isochronous transfers, split transactions (and therefore any
low-/full-speed device attached directly to an EHCI port), USBLEGSUP
BIOS/OS handoff, bandwidth-accurate scheduling and Interrupt Threshold
Control coalescing, and PORTSC's test-mode and wake-enable fields -- gaps
this emulation shares in spirit with `UhciController`'s own documented ones
above. Wiring EHCI's ports to their UHCI companion, the multifunction PCI
header and the snapshot section that carries both controllers' state are
`UhciCard`'s responsibility (`Devices/Usb/EhciController*.cs`'s own summary
names the API surface it exposes for that).

`EhciControllerTests` submits real QH/qTD structures through guest DMA,
including a high-speed control GET_DESCRIPTOR and a Bulk-Only Transport
READ(10) against `UsbMassStorageDevice`, port routing and PORTSC reset
behaviour, IOC/STALL/babble/timeout error paths, the IAAD doorbell, one
periodic interrupt queue head, the bus-master gate, and a snapshot round
trip. Reference: Intel Enhanced Host Controller Interface Specification for
Universal Serial Bus, Revision 1.0 (2002).
