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

The usual Windows 95 VM and its checkpoints have no configured shared
folders. In an isolated configured E: drive test, DOS 7 reported through
INT 21h/AH=52h a raw internal drive-table count of 32; the old guard
rejected that value above 26. The fix accepts internal counts from 1 through
32 while still capping
usable drive letters at A: through Z:. Tests exercise this through real guest
interrupts.

The real guest query now reports the correct CDS at `B7F0` with count 26.
Automatic attachment still retains an earlier CDS with count 3, and E: is
invalid in that state. This does not establish fully functional shared
folders, and the live 32-drive-table result does not support attributing the
failure to LASTDRIVE. End-to-end Windows 95 shared-folder operations remain
unverified.

`SharedFolderLettersTests` cover live CDS changes, occupied local/remote
letters, LASTDRIVE and exhaustion through Z:. `CompactDiscLetterTests` use
a guest interrupt handler reporting a CD letter different from the BIOS
estimate. `VmConfigurationTests` save, load and build a VM, then verify both
read-only enforcement and the writable default on the actual host files.
