# Shared folders

A VM can mount several host folders as DOS drive letters. A letter is stored
as a zero-based number in `vm.json`: 4 is E:, 5 is F:, and 25 is Z:.
For example, this configuration mounts `shared` beside the VM file as a
read-only E: drive:

```json
{
  "sharedFolders": [
    { "path": "shared", "drive": 4, "readOnly": true }
  ]
}
```

`readOnly` defaults to false for existing configurations. It is applied to
the host filesystem when either frontend opens the VM, and saving the VM
from the GUI retains it. A read-only share can be listed and read, but the
filesystem rejects modifications to host files. This is independent of the
host account's own filesystem permissions.

The GUI's Share Folder action and SDL's `--share /path` select a letter
automatically, starting at E:. After DOS is attached, selection reads its
current directory structure (CDS), skipping valid local drives, MSCDEX
letters, other redirectors and existing shares. It stays within LASTDRIVE
and never goes beyond Z:. The observed CD letter is also reserved with an
empty tray. If no letter is available, sharing reports an error instead of
creating a pending automatic mount outside the DOS drive table.

Before DOS starts, only configured disks, the CD and existing shares are
known. Set LASTDRIVE sufficiently high in CONFIG.SYS for startup shares.
Explicit letters in `vm.json` or `--share F:/path` still require the user to
avoid conflicts: validation of those mounts against drivers loaded later
in AUTOEXEC.BAT remains unfinished. Automatic allocation after attachment
does not establish that earlier explicit mounts are safe.

## Windows 95 status

Windows 95 DOS 7 startup has been checked with a configured E: share on an
isolated copy of the VM. Three changes make automatic attachment work:

- DOS can report 32 internal drive-table entries through INT 21h/AH=52h.
  The installer accepts that count while exposing only A: through Z:.
- Startup recognition accepts `C:\WINDOWS\COMMAND.COM`, including paths
  longer than the old sixteen-byte scan.
- After COMMAND.COM starts, attachment retries at the next execution batch
  while DOS is busy or its drive table is still changing. It hooks the
  redirector only after a safe query confirms a stable table, then returns
  to the normal polling interval.

In the measured boot, the live CDS changed from `80080` to `B7F0`, and the
redirector attached after two safe queries at 101.206 and 101.208 million
instructions. The earlier invalid E: drive was not evidence of a LASTDRIVE
configuration error.

Windows 95 4.00.1111 has been checked end to end: DIR and TYPE read the share
in a DOS window; COPY, REN and DEL create, rename and remove a fixture file;
Explorer opens E: and lists its contents; EXIT returns to the desktop. A
fresh restore with the share marked read-only still permits reads and denies
COPY without creating a host file. Source-file hashes remain unchanged.
The CD appears as F: in this configuration; its letter must not be assumed
to remain the same after adding a share.

DOS attribute changes remain incomplete. In the current implementation,
`ATTRIB +R` can report success while a subsequent attribute query still
reports only the archive bit. Persistent DOS attributes and their write
restrictions are being implemented separately. The verified read-only
configuration option above does not depend on ATTRIB.

`SharedFolderLettersTests` cover live CDS changes, occupied local/remote
letters, LASTDRIVE and exhaustion through Z:. `CompactDiscLetterTests` use
a guest interrupt handler reporting a CD letter different from the BIOS
estimate. `VmConfigurationTests` save, load and build a VM, then verify both
read-only enforcement and the writable default on the actual host files.
