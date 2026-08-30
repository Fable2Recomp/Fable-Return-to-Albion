#!/usr/bin/env python3
"""Print the D3DX constant table of a shipped Fable 2 shader blob.

The .bin blobs under assets/unpacked/shaders/blobs/shaders still carry their
full D3DX constant table — real member names, registers, counts and DEFAULT
VALUES that the recompiled HLSL loses. Everything is BIG-ENDIAN (Xbox 360).

Header: at the offset where be32[0] == 28 and be32[2] is 0xFFFF0300 (pixel)
or 0xFFFE0300 (vertex), seven be32s {size, creator, version, constants,
constantInfo, flags, target}. The corpus carries NO float defaults — only
BOOL/INT4 ones — so float constants are always runtime-fed.
Records: 20 bytes each at header+constantInfo,
{nameOff:4, regSet:2, regIdx:2, regCount:2, pad:2, typeInfoOff:4, defaultOff:4},
all offsets relative to the header. regSet: 0=BOOL 1=INT4 2=FLOAT4 3=SAMPLER.

Usage: parse_shader_ctab.py <blob.bin> [<blob.bin> ...]
"""
import struct
import sys

REG_SETS = {0: "bool", 1: "int4", 2: "float4", 3: "sampler"}


def parse_ctab(path):
    data = open(path, "rb").read()
    hdr = None
    for off in range(0, len(data) - 28, 4):
        d0, _, d2 = struct.unpack_from(">III", data, off)
        if d0 == 28 and d2 in (0xFFFF0300, 0xFFFE0300):
            hdr = off
            break
    if hdr is None:
        return None
    _, _, _, nconst, constinfo, _, _ = struct.unpack_from(">7I", data, hdr)
    out = []
    for i in range(nconst):
        rec = hdr + constinfo + i * 20
        name_off, reg_set, reg_idx, reg_count, _, type_off, def_off = struct.unpack_from(
            ">IHHHHII", data, rec)
        name = data[hdr + name_off:data.index(b"\0", hdr + name_off)].decode()
        defaults = None
        if def_off:
            _, _, rows, cols, elems, _ = struct.unpack_from(">6H", data, hdr + type_off)
            n = min(max(1, rows * cols * max(1, elems)), 256)
            defaults = struct.unpack_from(f">{n}f", data, hdr + def_off)
        out.append((name, reg_set, reg_idx, reg_count, defaults))
    return out


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    for path in sys.argv[1:]:
        rows = parse_ctab(path)
        if rows is None:
            print(f"== {path}: no constant table found")
            continue
        print(f"== {path}: {len(rows)} constants")
        for name, reg_set, reg_idx, reg_count, defaults in sorted(rows, key=lambda r: (r[1], r[2])):
            line = f"  {REG_SETS.get(reg_set, reg_set):<7} c{reg_idx:<3} x{reg_count:<3} {name}"
            if defaults is not None:
                line += "  defaults=" + str([round(v, 6) for v in defaults])
            print(line)


if __name__ == "__main__":
    main()
