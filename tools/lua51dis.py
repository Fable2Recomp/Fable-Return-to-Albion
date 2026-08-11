#!/usr/bin/env python3
"""Disassemble Fable 2's shipped Lua 5.1 bytecode.

The GUI ships as COMPILED chunks, not source, so the menu behaviour we want to reproduce
natively — item ordering, nesting, animation, what each entry does — is only readable through
the bytecode. `unluac` is the usual answer and is not available here; it also assumes an
8-byte double `lua_Number`, and these chunks use a 4-byte FLOAT, which is the one genuinely
non-standard thing about them. Every size is taken from the header rather than assumed, so
that variation costs nothing.

This is a DISASSEMBLER and deliberately not a decompiler. For working out behaviour, the
useful output is the shape: which globals a chunk touches, which methods it calls, and which
string constants it carries. That is what `--summary` prints, and it is enough to map the
menu model without reconstructing anyone's source.

Usage:
    lua51dis.py <file.lua> [--summary | --full]
"""

import struct
import sys

OPCODES = [
    "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL", "GETTABLE",
    "SETGLOBAL", "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF", "ADD", "SUB", "MUL",
    "DIV", "MOD", "POW", "UNM", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE",
    "TEST", "TESTSET", "CALL", "TAILCALL", "RETURN", "FORLOOP", "FORPREP",
    "TFORLOOP", "SETLIST", "CLOSE", "CLOSURE", "VARARG",
]

# Opcodes whose Bx indexes the constant table directly.
BX_IS_CONSTANT = {"LOADK", "GETGLOBAL", "SETGLOBAL"}

# Opcodes whose B and C are RK operands — a register below 256, a constant above it.
# SETTABLE is the one that matters: it is how a Lua constants table is written.
RK_BC = {"SETTABLE"}


class Reader:
    def __init__(self, data):
        self.data = data
        self.at = 0
        # Filled in by header(); nothing below assumes a width.
        self.endian = "<"
        self.int_size = 4
        self.size_t = 4
        self.number_size = 8
        self.integral = False

    def take(self, n):
        if self.at + n > len(self.data):
            raise EOFError(f"ran off the end at {self.at} wanting {n}")
        chunk = self.data[self.at:self.at + n]
        self.at += n
        return chunk

    def byte(self):
        return self.take(1)[0]

    def integer(self):
        fmt = {1: "b", 2: "h", 4: "i", 8: "q"}[self.int_size]
        return struct.unpack(self.endian + fmt, self.take(self.int_size))[0]

    def size(self):
        fmt = {1: "B", 2: "H", 4: "I", 8: "Q"}[self.size_t]
        return struct.unpack(self.endian + fmt, self.take(self.size_t))[0]

    def number(self):
        if self.integral:
            fmt = {4: "i", 8: "q"}[self.number_size]
        else:
            fmt = {4: "f", 8: "d"}[self.number_size]
        return struct.unpack(self.endian + fmt, self.take(self.number_size))[0]

    def string(self):
        length = self.size()
        if length == 0:
            return None
        # The stored length includes the terminating NUL.
        return self.take(length)[:-1].decode("utf-8", "replace")

    def header(self):
        if self.take(4) != b"\x1bLua":
            raise ValueError("not a Lua chunk")
        version = self.byte()
        if version != 0x51:
            raise ValueError(f"expected Lua 5.1, got 0x{version:02X}")
        self.byte()                       # format
        self.endian = "<" if self.byte() else ">"
        self.int_size = self.byte()
        self.size_t = self.byte()
        instruction_size = self.byte()
        self.number_size = self.byte()
        self.integral = self.byte() != 0
        if instruction_size != 4:
            raise ValueError(f"unsupported instruction size {instruction_size}")
        return {
            "endian": "little" if self.endian == "<" else "big",
            "int": self.int_size, "size_t": self.size_t,
            "number": self.number_size, "integral": self.integral,
        }


class Function:
    def __init__(self):
        self.source = None
        self.line_defined = 0
        self.upvalue_count = 0
        self.parameter_count = 0
        self.is_vararg = 0
        self.code = []
        self.constants = []
        self.protos = []
        self.upvalues = []


def read_function(r):
    f = Function()
    f.source = r.string()
    f.line_defined = r.integer()
    r.integer()                            # last line defined
    f.upvalue_count = r.byte()
    f.parameter_count = r.byte()
    f.is_vararg = r.byte()
    r.byte()                               # max stack size

    for _ in range(r.integer()):
        f.code.append(struct.unpack(r.endian + "I", r.take(4))[0])

    for _ in range(r.integer()):
        kind = r.byte()
        if kind == 0:
            f.constants.append(None)
        elif kind == 1:
            f.constants.append(r.byte() != 0)
        elif kind == 3:
            f.constants.append(r.number())
        elif kind == 4:
            f.constants.append(r.string())
        else:
            raise ValueError(f"unknown constant type {kind}")

    for _ in range(r.integer()):
        f.protos.append(read_function(r))

    # Debug section. Line info and local names are not needed for behaviour, but they have
    # to be consumed or every following function reads from the wrong offset.
    for _ in range(r.integer()):
        r.integer()
    for _ in range(r.integer()):
        r.string(); r.integer(); r.integer()
    for _ in range(r.integer()):
        f.upvalues.append(r.string())

    return f


def decode(instruction):
    op = instruction & 0x3F
    return {
        "op": OPCODES[op] if op < len(OPCODES) else f"OP{op}",
        "A": (instruction >> 6) & 0xFF,
        "B": (instruction >> 23) & 0x1FF,
        "C": (instruction >> 14) & 0x1FF,
        "Bx": (instruction >> 14) & 0x3FFFF,
    }


def constant(f, index):
    if 0 <= index < len(f.constants):
        return f.constants[index]
    return f"?{index}"


def rk(f, value):
    """Lua 5.1 packs 'register or constant' into one field: >=256 means constant."""
    return constant(f, value - 256) if value >= 256 else None


def walk(f, path="main"):
    yield path, f
    for i, proto in enumerate(f.protos):
        yield from walk(proto, f"{path}.{i}")


def summarise(f):
    """Globals touched, methods called and strings carried, per function."""
    for path, fn in walk(f):
        reads, writes, methods, strings = [], [], [], []

        for instruction in fn.code:
            d = decode(instruction)
            if d["op"] == "GETGLOBAL":
                reads.append(str(constant(fn, d["Bx"])))
            elif d["op"] == "SETGLOBAL":
                writes.append(str(constant(fn, d["Bx"])))
            elif d["op"] == "SELF":
                name = rk(fn, d["C"])
                if name is not None:
                    methods.append(str(name))
            elif d["op"] in ("GETTABLE", "SETTABLE"):
                key = rk(fn, d["C"] if d["op"] == "GETTABLE" else d["B"])
                if isinstance(key, str):
                    strings.append(key)

        literals = [c for c in fn.constants if isinstance(c, str)]
        # Numbers matter as much as names here: a constants chunk is mostly timings and
        # thresholds, and "there is a FIRST_NAVIGATE_INTERVAL" is useless without its value.
        numbers = [c for c in fn.constants
                   if isinstance(c, (int, float)) and not isinstance(c, bool)]
        if not (reads or writes or methods or literals or numbers):
            continue

        print(f"\n--- {path}  ({len(fn.code)} instructions, "
              f"{fn.parameter_count} param(s), {fn.upvalue_count} upvalue(s))")
        if reads:
            print(f"    reads globals : {', '.join(sorted(set(reads)))}")
        if writes:
            print(f"    WRITES globals: {', '.join(sorted(set(writes)))}")
        if methods:
            print(f"    calls methods : {', '.join(sorted(set(methods)))}")
        if strings:
            print(f"    table keys    : {', '.join(sorted(set(strings))[:24])}")
        if literals:
            print(f"    strings       : {', '.join(sorted(set(literals))[:24])}")
        if numbers:
            shown = sorted(set(numbers))[:24]
            print(f"    numbers       : "
                  f"{', '.join(f'{n:g}' for n in shown)}")

        # A constants table is written as `NAME = value` pairs, so pairing the SETTABLE key
        # order against the LOADK order recovers the assignment — which is the only way to
        # tell which timing belongs to which name.
        pairs = []
        pending_key = None
        for instruction in fn.code:
            d = decode(instruction)
            if d["op"] == "LOADK":
                value = constant(fn, d["Bx"])
                if isinstance(value, (int, float)) and not isinstance(value, bool):
                    pending_key = value
            elif d["op"] == "SETTABLE":
                key = rk(fn, d["B"])
                if isinstance(key, str) and pending_key is not None:
                    pairs.append(f"{key}={pending_key:g}")
                    pending_key = None

        if pairs:
            print(f"    assignments   : {', '.join(pairs[:24])}")


def full(f):
    for path, fn in walk(f):
        print(f"\n=== {path}  ({len(fn.code)} instructions) ===")
        for i, instruction in enumerate(fn.code):
            d = decode(instruction)
            note = ""
            if d["op"] in BX_IS_CONSTANT:
                note = f"  ; {constant(fn, d['Bx'])!r}"
            elif d["op"] == "SELF":
                note = f"  ; :{rk(fn, d['C'])}"
            elif d["op"] in RK_BC:
                # A constants chunk writes `NAME = value` as one SETTABLE with BOTH operands
                # constant, so neither shows up as a LOADK. Without resolving them here the
                # timings and slot tables these files exist to carry are unreadable — the
                # --summary pairing only catches the LOADK form and silently drops the rest.
                key = rk(fn, d["B"])
                value = rk(fn, d["C"])
                shown = [f"{part!r}" for part in (key, value)
                         if part is not None]
                if shown:
                    note = f"  ; {' = '.join(shown)}"
            print(f"    {i:5}  {d['op']:<10} A={d['A']:<4}B={d['B']:<4}"
                  f"C={d['C']:<4}Bx={d['Bx']}{note}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    with open(sys.argv[1], "rb") as handle:
        data = handle.read()

    r = Reader(data)
    info = r.header()
    print(f"[lua51] {sys.argv[1]}")
    print(f"[lua51] {info['endian']}-endian, int={info['int']}, size_t={info['size_t']}, "
          f"lua_Number={info['number']} "
          f"({'integer' if info['integral'] else 'float'})")

    top = read_function(r)
    if top.source:
        print(f"[lua51] source: {top.source}")

    if "--full" in sys.argv:
        full(top)
    else:
        summarise(top)
    return 0


if __name__ == "__main__":
    sys.exit(main())
