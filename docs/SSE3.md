# SSE3

Core2 and Haswell implement the SSE3 instructions introduced with Prescott
in 2004. The catalog's Pentium 4 remains Northwood, without SSE3.

- HADDPS/HADDPD and HSUBPS/HSUBPD reduce adjacent lanes from the old
  destination and then the source. ADDSUBPS/ADDSUBPD subtract even lanes
  and add odd lanes. Every lane contributes to MXCSR before a single
  destination commit; unmasked exceptions leave all destination bits intact.
- MOVSLDUP and MOVSHDUP duplicate the even and odd single lanes;
  MOVDDUP duplicates the low quadword. These are bit copies, including
  NaN payloads, and do not alter MXCSR. MOVDDUP reads only eight memory bytes.
- LDDQU reads exactly the requested sixteen bytes, without speculative
  cache-line overfetch. Its memory operand need not be sixteen-byte aligned.
  Segment and paging failures leave XMM unchanged. The existing qword
  alignment-check policy still applies when CPL3, AM and AC enable #AC.
- FISTTP uses the x87 environment rather than MXCSR and does not require
  CR4.OSFXSR. Its integer widths and completion rules are described in
  [X87.md](X87.md). Redundant 66/F2/F3 prefixes do not select its width;
  the DB/DD/DF opcode does.

The aligned XMM forms require sixteen-byte alignment for memory sources.
The existing policy rejects conflicting XMM mandatory prefixes, while
FISTTP follows the independently measured x87 prefix behavior.
MONITOR/MWAIT are not part of the advertised SSE3 bit and remain unsupported.

The instruction definitions follow the [Intel software developer manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html).
Directed Raw80 probes and the XMM differential oracle used an AMD Ryzen 7
7700; they are not measurements of historical Prescott or Core2 silicon.
The oracle families cover register and memory sources, aliases, NaN payloads,
denormals and MXCSR rounding/FZ/DAZ choices with masked exceptions. Unit
tests cover unmasked completion, feature gating and page/segment faults.
