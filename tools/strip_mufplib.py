#!/usr/bin/env python3
"""Strip the floating point library (mufplib) out of an RP2040 bootrom image.

The RP2040 bootrom is BSD-3-Clause (https://github.com/raspberrypi/pico-bootrom),
but its LICENSE.TXT excludes bootrom/mufplib.S and bootrom/mufplib-double.S, which
carry a separate licence: they may be used "solely on a Raspberry Pi RP2040 device",
or under GPLv2 from their author (© 2020 Mark Owen, https://www.quinapalus.com).
Neither permits redistributing them inside this emulator, so they are removed from
the images we ship. RP2040Sharp implements the ROM float API natively instead — see
src/RP2040Sharp/Peripherals/BootromFloat.cs and NOTICE.txt.

The ROM's own data table delimits the library: FS..FE (single) and DS..DE (double).
Those markers, and the 'SF'/'SD' function tables that point into the region, are
data and stay put — the emulator hooks the addresses the tables name. Only the code
is blanked, and the image keeps its 16 KB size so every ROM address stays valid.

Usage:  python3 tools/strip_mufplib.py src/RP2040Sharp/bootrom_b1.bin [...]
        python3 tools/strip_mufplib.py --check src/RP2040Sharp/bootrom_b1.bin
"""

import struct
import sys

ROM_SIZE = 16384
FILL = b"\x00\xbe"  # BKPT #0 — traps loudly if an entry is ever left unhooked


def data_table(rom):
    """Return the ROM data table as {code: value}, per RP2040 datasheet §2.8.3."""
    entries = {}
    off = struct.unpack_from("<H", rom, 0x16)[0]
    while off < len(rom) - 4:
        code = rom[off:off + 2]
        if code == b"\x00\x00":
            break
        entries[code.decode("latin1")] = struct.unpack_from("<H", rom, off + 2)[0]
        off += 4
    return entries


def float_region(rom):
    """Return (start, end) of the float library code, from the ROM's own markers."""
    e = data_table(rom)
    missing = {"FS", "FE", "DS", "DE"} - e.keys()
    if missing:
        raise SystemExit(f"error: ROM has no {'/'.join(sorted(missing))} marker; not an RP2040 bootrom?")
    start, end = e["FS"], e["DE"]
    if not 0 < start < end <= ROM_SIZE:
        raise SystemExit(f"error: implausible float region {start:#x}-{end:#x}")
    return start, end


def non_float_functions_in(rom, start, end):
    """Any ROM *function* entry pointing into the region would make it unsafe to blank."""
    hits = []
    off = struct.unpack_from("<H", rom, 0x14)[0]
    while off < len(rom) - 4:
        code = rom[off:off + 2]
        if code == b"\x00\x00":
            break
        addr = struct.unpack_from("<H", rom, off + 2)[0] & ~1
        if start <= addr < end:
            hits.append((code.decode("latin1"), addr))
        off += 4
    return hits


def process(path, check_only):
    rom = bytearray(open(path, "rb").read())
    if len(rom) != ROM_SIZE:
        raise SystemExit(f"error: {path} is {len(rom)} bytes, expected {ROM_SIZE}")

    start, end = float_region(rom)
    size = end - start

    clash = non_float_functions_in(rom, start, end)
    if clash:
        raise SystemExit(f"error: {path}: non-float ROM functions inside the region: {clash}")

    already = rom[start:end] == FILL * (size // 2)
    if check_only:
        state = "stripped" if already else "PRESENT"
        print(f"{path}: float region {start:#x}-{end:#x} ({size} bytes) — mufplib {state}")
        return 0 if already else 1

    if already:
        print(f"{path}: already stripped, nothing to do")
        return 0

    rom[start:end] = FILL * (size // 2)
    if size % 2:  # odd-sized region: pad the final byte
        rom[end - 1] = 0
    open(path, "wb").write(rom)
    print(f"{path}: stripped {size} bytes ({size * 100 / ROM_SIZE:.1f}% of the ROM) at {start:#x}-{end:#x}")
    return 0


def main(argv):
    check_only = "--check" in argv
    paths = [a for a in argv[1:] if not a.startswith("--")]
    if not paths:
        raise SystemExit(__doc__)
    return max(process(p, check_only) for p in paths)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
