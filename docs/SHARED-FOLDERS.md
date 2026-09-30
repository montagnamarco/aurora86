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

Windows 95 4.00.1111 has been checked end to end: DIR and TYPE read the share
in a DOS window; COPY, REN and DEL create, rename and remove a fixture file;
Explorer opens E: and lists its contents; EXIT returns to the desktop. A
fresh restore with the share marked read-only still permits reads and denies
COPY without creating a host file. Source-file hashes remain unchanged.
The CD appears as F: in this configuration; its letter must not be assumed
to remain the same after adding a share.

DOS Read-only, Hidden, System and Archive attributes persist in an owned
`.aurora386-dos-attributes.json` file at the share root. The backend creates
it only when a writable operation needs metadata; mounting, listing and
reading a share create nothing. Host permissions are preserved. A file with
that name is private only when it has the recognized format and version.
An unrelated existing file remains visible and is never overwritten; changes
that require metadata fail with DOS access denied until the collision is
resolved. The owned file is excluded from guest listings and path lookup,
including DOS short-name aliases.

Windows 95 `ATTRIB +R` now reads back as A R after a fresh machine restore.
DELETE and an explicitly confirmed COPY overwrite both return access denied.
A read-only share rejects `ATTRIB -R`; on a writable share, clearing R reads
back as A and permits deletion. Handles opened for writing before R was set
remain writable, matching the measured local DOS 7 FAT behavior. Existing
directory identity is preserved, the volume-label bit is ignored by the
setter, and invalid attribute masks return access denied.

Metadata follows guest renames, including directory descendants, and is
removed with deleted entries. Metadata is saved atomically before destructive
host changes; if a host operation fails, the backend attempts to restore the
previous metadata and reports failures to DOS. This is runtime rollback,
not a crash-atomic transaction across the metadata and host file. External
host renames are not tracked. Unreadable metadata also returns a DOS error
instead of escaping the guest interrupt as a host exception.

Automatic Archive updates after writes remain unfinished: the measured local
DOS 7 filesystem sets A after a write and close, while the shared-folder
backend currently retains explicitly stored flags. The persistent attribute
setter and read-only restrictions above do not establish that behavior.

`SharedFolderLettersTests` cover live CDS changes, occupied local/remote
letters, LASTDRIVE and exhaustion through Z:. `CompactDiscLetterTests` use
a guest interrupt handler reporting a CD letter different from the BIOS
estimate. `VmConfigurationTests` save, load and build a VM, then verify both
read-only enforcement and the writable default on the actual host files.
