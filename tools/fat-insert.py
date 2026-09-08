r"""Puts a file into a FAT16 disk image, allocating as many clusters as it needs.

Written to get a Windows driver into the reference image without booting the
guest to do it, which is a chicken-and-egg problem: the driver is what the
guest needs in order to be worth booting.

The interesting part of the format is the chain. A directory entry says only
where a file starts; each FAT entry says where it continues, and the last one
says it ends. So a file of thirteen clusters means thirteen FAT entries
pointing at one another, and getting one wrong produces a file that reads
correctly up to that point and then turns into whatever else was on the disk.

Both copies of the FAT are written. DOS reads the first and checks the second,
and a mismatch is what CHKDSK reports as a cross-linked file.

Usage: fat-insert.py <image> <path\in\image> <source file>
       fat-insert.py <image> --mkdir <path\in\image>
"""
import struct, sys

MAKE_DIRECTORY = sys.argv[2] == '--mkdir'
if MAKE_DIRECTORY:
    IMAGE, PATH_IN_IMAGE, SOURCE = sys.argv[1], sys.argv[3], None
else:
    IMAGE, PATH_IN_IMAGE, SOURCE = sys.argv[1], sys.argv[2], sys.argv[3]

f = open(IMAGE, 'r+b')
mbr = f.read(512)
base = struct.unpack_from('<I', mbr, 0x1BE + 8)[0] * 512
f.seek(base); bpb = f.read(512)
bps, spc, rsv, nfat, nroot, tot, med, spf = struct.unpack_from('<HBHBHHBH', bpb, 11)

fat_off  = base + rsv * bps
root_off = fat_off + nfat * spf * bps
data_off = root_off + nroot * 32
cluster  = spc * bps

f.seek(fat_off); fat = bytearray(f.read(spf * bps))

def entry(c):     return struct.unpack_from('<H', fat, c * 2)[0]
def set_entry(c, v): struct.pack_into('<H', fat, c * 2, v)

def read_chain(c, size=None):
    out = bytearray()
    while 2 <= c < 0xFFF8:
        f.seek(data_off + (c - 2) * cluster); out += f.read(cluster)
        c = entry(c)
    return bytes(out[:size]) if size else bytes(out)

def entries(data):
    out = []
    for i in range(0, len(data), 32):
        e = data[i:i+32]
        if e[0] == 0: break
        if e[0] == 0xE5 or e[11] & 0x08: continue
        name = e[0:8].decode('latin1').rstrip()
        ext  = e[8:11].decode('latin1').rstrip()
        out.append((name + ('.' + ext if ext else ''), e[11],
                    struct.unpack_from('<H', e, 26)[0],
                    struct.unpack_from('<I', e, 28)[0], i))
    return out

# Walk the path down to the directory the file belongs in.
parts = PATH_IN_IMAGE.strip('\\').split('\\')
directory_off, directory_size = root_off, nroot * 32

for part in parts[:-1]:
    f.seek(directory_off); data = f.read(directory_size)
    found = next((v for v in entries(data) if v[0].upper() == part.upper() and v[1] & 0x10), None)
    if found is None: raise SystemExit(f"no such directory: {part}")
    directory_off = data_off + (found[2] - 2) * cluster
    directory_size = len(read_chain(found[2]))

if MAKE_DIRECTORY:
    # A directory is a file whose content is directory entries, and whose
    # first two entries are "." and ".." -- not a convention DOS could do
    # without: ".." is the only record of where the directory came from, and
    # a CHDIR .. with it missing has nowhere to go.
    name = parts[-1].upper()
    f.seek(directory_off); d = bytearray(f.read(directory_size))
    if any(v[0].upper() == name for v in entries(bytes(d))):
        raise SystemExit(f"already there: {name}")

    free = [c for c in range(2, (spf * bps) // 2) if entry(c) == 0][:1]
    if not free: raise SystemExit("not enough free space")
    set_entry(free[0], 0xFFFF)

    # The parent's cluster number, which is 0 when the parent is the root:
    # the root has no cluster of its own on FAT16, it sits in its own region
    # before the data area, so ".." in a top-level directory points at zero.
    parent = 0 if directory_off == root_off else (directory_off - data_off) // cluster + 2

    block = bytearray(cluster)
    dot = ('.'.ljust(8) + ' ' * 3).encode('latin1') + bytes([0x10]) + b'\0' * 10
    dot += struct.pack('<HH', 0, 0) + struct.pack('<HI', free[0], 0)
    dotdot = ('..'.ljust(8) + ' ' * 3).encode('latin1') + bytes([0x10]) + b'\0' * 10
    dotdot += struct.pack('<HH', 0, 0) + struct.pack('<HI', parent, 0)
    block[0:32] = dot
    block[32:64] = dotdot
    f.seek(data_off + (free[0] - 2) * cluster); f.write(block)

    record = (name.ljust(8)[:8] + ' ' * 3).encode('latin1')
    record += bytes([0x10]) + b'\0' * 10          # directory attribute
    record += struct.pack('<HH', 0, 0)
    record += struct.pack('<HI', free[0], 0)      # a directory's size is zero

    for i in range(0, len(d), 32):
        if d[i] in (0x00, 0xE5):
            d[i:i+32] = record
            break
    else:
        raise SystemExit("no free directory entry")

    f.seek(directory_off); f.write(d)
    for n in range(nfat):
        f.seek(fat_off + n * spf * bps); f.write(fat)
    f.close()
    print(f"made directory {name} at cluster {free[0]}")
    sys.exit()

content = open(SOURCE, 'rb').read()
needed = (len(content) + cluster - 1) // cluster

# A free cluster is one whose FAT entry is zero.
free = [c for c in range(2, (spf * bps) // 2) if entry(c) == 0][:needed]
if len(free) < needed: raise SystemExit("not enough free space")

for i, c in enumerate(free):
    # Each entry points at the next; the last says the file ends here.
    set_entry(c, free[i+1] if i + 1 < len(free) else 0xFFFF)
    piece = content[i*cluster:(i+1)*cluster]
    f.seek(data_off + (c - 2) * cluster)
    f.write(piece + b'\0' * (cluster - len(piece)))

# The directory entry: an 8.3 name, upper case and space padded, which is
# not a display convention but the storage format itself.
filename = parts[-1].upper()
name, _, ext = filename.partition('.')
record = (name.ljust(8)[:8] + ext.ljust(3)[:3]).encode('latin1')
record += bytes([0x20]) + b'\0' * 10                   # archive attribute
record += struct.pack('<HH', 0, 0)                     # time and date
record += struct.pack('<HI', free[0], len(content))

f.seek(directory_off); d = bytearray(f.read(directory_size))
for i in range(0, len(d), 32):
    if d[i] in (0x00, 0xE5):        # never used, or deleted
        d[i:i+32] = record
        break
else:
    raise SystemExit("no free directory entry")

f.seek(directory_off); f.write(d)

# Both copies of the FAT.
for n in range(nfat):
    f.seek(fat_off + n * spf * bps); f.write(fat)

f.close()
print(f"wrote {filename}: {len(content)} bytes in {needed} clusters from {free[0]}")
