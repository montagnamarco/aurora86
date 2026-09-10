# Trio64 Feature Control

The implementation follows the Feature Control register in the
[S3 Trio32/Trio64 data book, section 14](https://www.bitsavers.org/components/s3/DB014-B_Trio32_Trio64_Graphics_Accelerators_Mar1995.pdf).
Bit 3 (VSSL) is writable; reserved bits read as zero. Miscellaneous Output
bit 0 selects the write address: 3DAh for colour, 3BAh for monochrome.
The register is read at 3CAh.

Writes to the inactive address are ignored. Writing Feature Control does
not reset the attribute controller flip-flop: that side effect belongs to
reading Input Status 1 at the shared address. VSSL selects the signal on
the physical VSYNC output and does not alter internal retrace status. The
framebuffer renderer does not model an external monitor's sync circuitry.

Snapshots use one previously zero-filled byte of the existing register
block. Old snapshots therefore restore Feature Control to its reset value
without changing the snapshot format. `VgaFeatureControlTests` verifies
both address selections, reserved bits, the shared-port side effects and
snapshot round trips.
