#!/usr/bin/env python3
"""Disassemble the ActionScript 2 (AVM1) bytecode inside a Flash SWF.

THE SWF IS THE SPECIFICATION for the pub games. Keystone, Mage Dice and Wizard's
Tower ship as `game.swf` under `<gamedata>/data/pubgames/<name>/`, and the whole
of their game logic is AS2 bytecode in the file's `DoInitAction` tags — 37
classes and 232 KB of it for Keystone alone. The C# rebuild under
`Godot/FlashGames/` is a port of exactly that, so "is the port complete and
correct" is a question this tool answers and nothing else does.

Written because no decompiler is installed here and the alternative — reading
the port and reasoning about what it probably says — is how a coherent but wrong
reconstruction gets shipped.

Usage:
    as2dis.py <game.swf>                     list every class and its methods
    as2dis.py <game.swf> --class Keystone    disassemble one class
    as2dis.py <game.swf> --class Keystone --method GameLoop
    as2dis.py <game.swf> --class Keystone --method GameLoop --pseudo
    as2dis.py <game.swf> --pool              dump constant pools only
    as2dis.py <game.swf> --raw               every action block, undivided

`--pseudo` reassembles the stack machine into expressions, which is the mode to
read a method in. AVM1 is a stack machine, so a single line of source becomes a
dozen pushes and a CallMethod; at 232 KB per game, reading that as opcodes is not
a realistic way to check a port. Branches are left as explicit gotos rather than
guessed back into if/while — a wrong loop reconstruction reads as confidently as
a right one, and the labels are unambiguous.
"""

from __future__ import annotations

import argparse
import struct
import sys
import zlib

# --------------------------------------------------------------------------
# SWF container
# --------------------------------------------------------------------------


def read_swf(path: str) -> bytes:
    """The tag stream, decompressed. Returns the bytes AFTER the 8-byte header."""
    data = open(path, "rb").read()
    signature, body = data[:3], data[8:]
    if signature == b"CWS":
        body = zlib.decompress(body)
    elif signature == b"ZWS":
        raise SystemExit("LZMA-compressed SWF; not supported")
    elif signature != b"FWS":
        raise SystemExit(f"not a SWF: {signature!r}")
    return body


def _rect_length(body: bytes) -> int:
    """The RECT at the start of the tag stream is bit-packed and variable width."""
    bits = body[0] >> 3
    return ((5 + bits * 4) + 7) // 8


def iter_tags(body: bytes, offset: int = 0, end: int | None = None):
    """Yield (code, payload). Recurses into DefineSprite, which nests tags."""
    end = len(body) if end is None else end
    i = offset
    while i < end - 1:
        header = struct.unpack_from("<H", body, i)[0]
        i += 2
        code, length = header >> 6, header & 0x3F
        if length == 0x3F:
            length = struct.unpack_from("<I", body, i)[0]
            i += 4
        payload = body[i : i + length]
        yield code, payload
        if code == 39:  # DefineSprite: header is id + frame count, then tags
            yield from iter_tags(payload, 4)
        i += length
        if code == 0:
            break


def tag_stream(body: bytes):
    return iter_tags(body, _rect_length(body) + 4)


# --------------------------------------------------------------------------
# AVM1
# --------------------------------------------------------------------------

# Actions with no payload. Everything >= 0x80 carries a little-endian length.
SIMPLE = {
    0x04: "NextFrame",
    0x05: "PreviousFrame",
    0x06: "Play",
    0x07: "Stop",
    0x08: "ToggleQuality",
    0x09: "StopSounds",
    0x0A: "Add",
    0x0B: "Subtract",
    0x0C: "Multiply",
    0x0D: "Divide",
    0x0E: "Equals",
    0x0F: "Less",
    0x10: "And",
    0x11: "Or",
    0x12: "Not",
    0x13: "StringEquals",
    0x14: "StringLength",
    0x15: "StringExtract",
    0x17: "Pop",
    0x18: "ToInteger",
    0x1C: "GetVariable",
    0x1D: "SetVariable",
    0x20: "SetTarget2",
    0x21: "StringAdd",
    0x22: "GetProperty",
    0x23: "SetProperty",
    0x24: "CloneSprite",
    0x25: "RemoveSprite",
    0x26: "Trace",
    0x27: "StartDrag",
    0x28: "EndDrag",
    0x29: "StringLess",
    0x2A: "Throw",
    0x2B: "CastOp",
    0x2C: "ImplementsOp",
    0x30: "RandomNumber",
    0x31: "MBStringLength",
    0x32: "CharToAscii",
    0x33: "AsciiToChar",
    0x34: "GetTime",
    0x35: "MBStringExtract",
    0x36: "MBCharToAscii",
    0x37: "MBAsciiToChar",
    0x3A: "Delete",
    0x3B: "Delete2",
    0x3C: "DefineLocal",
    0x3D: "CallFunction",
    0x3E: "Return",
    0x3F: "Modulo",
    0x40: "NewObject",
    0x41: "DefineLocal2",
    0x42: "InitArray",
    0x43: "InitObject",
    0x44: "TypeOf",
    0x45: "TargetPath",
    0x46: "Enumerate",
    0x47: "Add2",
    0x48: "Less2",
    0x49: "Equals2",
    0x4A: "ToNumber",
    0x4B: "ToString",
    0x4C: "PushDuplicate",
    0x4D: "StackSwap",
    0x4E: "GetMember",
    0x4F: "SetMember",
    0x50: "Increment",
    0x51: "Decrement",
    0x52: "CallMethod",
    0x53: "NewMethod",
    0x54: "InstanceOf",
    0x55: "Enumerate2",
    0x60: "BitAnd",
    0x61: "BitOr",
    0x62: "BitXor",
    0x63: "BitLShift",
    0x64: "BitRShift",
    0x65: "BitURShift",
    0x66: "StrictEquals",
    0x67: "Greater",
    0x68: "StringGreater",
    0x69: "Extends",
}

# Property ids used by GetProperty/SetProperty, which take a NUMBER not a name.
PROPERTIES = {
    0: "_x", 1: "_y", 2: "_xscale", 3: "_yscale", 4: "_currentframe",
    5: "_totalframes", 6: "_alpha", 7: "_visible", 8: "_width", 9: "_height",
    10: "_rotation", 11: "_target", 12: "_framesloaded", 13: "_name",
    14: "_droptarget", 15: "_url", 16: "_highquality", 17: "_focusrect",
    18: "_soundbuftime", 19: "_quality", 20: "_xmouse", 21: "_ymouse",
}


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.i = 0

    def u8(self) -> int:
        v = self.data[self.i]
        self.i += 1
        return v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self.data, self.i)[0]
        self.i += 2
        return v

    def s16(self) -> int:
        v = struct.unpack_from("<h", self.data, self.i)[0]
        self.i += 2
        return v

    def s32(self) -> int:
        v = struct.unpack_from("<i", self.data, self.i)[0]
        self.i += 4
        return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.data, self.i)[0]
        self.i += 4
        return v

    def f32(self) -> float:
        v = struct.unpack_from("<f", self.data, self.i)[0]
        self.i += 4
        return v

    def f64(self) -> float:
        # AVM1 doubles are stored as two 32-bit words, HIGH WORD FIRST. Reading
        # them as a plain little-endian double gives garbage magnitudes.
        hi = struct.unpack_from("<I", self.data, self.i)[0]
        lo = struct.unpack_from("<I", self.data, self.i + 4)[0]
        self.i += 8
        return struct.unpack("<d", struct.pack("<II", lo, hi))[0]

    def zstr(self) -> str:
        end = self.data.index(b"\0", self.i)
        s = self.data[self.i : end].decode("utf-8", "replace")
        self.i = end + 1
        return s


def _quote(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'


def _push_operands(r: Reader, end: int, pool: list[str]) -> list[str]:
    """Push carries a packed list of typed values, not one."""
    out = []
    while r.i < end:
        kind = r.u8()
        if kind == 0:
            out.append(_quote(r.zstr()))
        elif kind == 1:
            out.append(repr(r.f32()))
        elif kind == 2:
            out.append("null")
        elif kind == 3:
            out.append("undefined")
        elif kind == 4:
            out.append(f"r{r.u8()}")
        elif kind == 5:
            out.append("true" if r.u8() else "false")
        elif kind == 6:
            out.append(repr(r.f64()))
        elif kind == 7:
            out.append(str(r.s32()))
        elif kind == 8:
            k = r.u8()
            out.append(_quote(pool[k]) if k < len(pool) else f"const{k}")
        elif kind == 9:
            k = r.u16()
            out.append(_quote(pool[k]) if k < len(pool) else f"const{k}")
        else:
            out.append(f"<type {kind}>")
            break
    return out


REGISTER_PRESET = [
    None, "this", "arguments", "super", "_root", "_parent", "_global",
]


class Function:
    """One DefineFunction/DefineFunction2 body, with the code it encloses."""

    def __init__(self, name: str, params: list[str], start: int, size: int,
                 registers: dict[int, str], flags: int):
        self.name = name
        self.params = params
        self.start = start          # offset of the first action INSIDE the body
        self.size = size            # code size in bytes
        self.registers = registers
        self.flags = flags
        self.lines: list[str] = []

    @property
    def end(self) -> int:
        return self.start + self.size

    def signature(self) -> str:
        return f"{self.name or '<anonymous>'}({', '.join(self.params)})"


def disassemble(code: bytes, pool_out: list[str] | None = None):
    """Return (lines, functions, pool). Lines are (offset, text) in order."""
    r = Reader(code)
    pool: list[str] = []
    lines: list[tuple[int, str]] = []
    functions: list[Function] = []
    targets: set[int] = set()

    while r.i < len(code):
        offset = r.i
        action = r.u8()
        if action == 0:
            lines.append((offset, "End"))
            break

        if action < 0x80:
            lines.append((offset, SIMPLE.get(action, f"Unknown_{action:02X}")))
            continue

        length = r.u16()
        end = r.i + length
        text = f"Unknown_{action:02X}"

        if action == 0x88:  # ConstantPool
            count = r.u16()
            pool = [r.zstr() for _ in range(count)]
            text = f"ConstantPool  ({count} entries)"
        elif action == 0x96:  # Push
            text = "Push          " + ", ".join(_push_operands(r, end, pool))
        elif action == 0x99:  # Jump
            delta = r.s16()
            targets.add(end + delta)
            text = f"Jump          -> {end + delta:#06x}"
        elif action == 0x9D:  # If
            delta = r.s16()
            targets.add(end + delta)
            text = f"If            -> {end + delta:#06x}"
        elif action == 0x9E:  # Call
            text = "Call"
        elif action == 0x9A:  # GetURL2
            text = f"GetURL2       flags={r.u8():#04x}"
        elif action == 0x8A:  # WaitForFrame
            text = f"WaitForFrame  frame={r.u16()} skip={r.u8()}"
        elif action == 0x8D:  # WaitForFrame2
            text = f"WaitForFrame2 skip={r.u8()}"
        elif action == 0x8B:  # SetTarget
            text = f"SetTarget     {_quote(r.zstr())}"
        elif action == 0x8C:  # GoToLabel
            text = f"GoToLabel     {_quote(r.zstr())}"
        elif action == 0x81:  # GotoFrame
            text = f"GotoFrame     {r.u16()}"
        elif action == 0x83:  # GetURL
            url = r.zstr()
            text = f"GetURL        {_quote(url)}, {_quote(r.zstr())}"
        elif action == 0x87:  # StoreRegister
            text = f"StoreRegister r{r.u8()}"
        elif action == 0x8F:  # Try
            flags = r.u8()
            try_size, catch_size, finally_size = r.u16(), r.u16(), r.u16()
            catch = f"r{r.u8()}" if flags & 0x04 else r.zstr()
            text = (f"Try           catch={catch} sizes=({try_size},"
                    f"{catch_size},{finally_size})")
        elif action == 0x94:  # With
            text = f"With          size={r.u16()}"
        elif action in (0x9B, 0x8E):  # DefineFunction / DefineFunction2
            name = r.zstr()
            if action == 0x9B:
                count = r.u16()
                params = [r.zstr() for _ in range(count)]
                registers: dict[int, str] = {}
                flags = 0
                body = r.u16()
            else:
                count = r.u16()
                register_count = r.u8()
                flags = r.u16()
                params = []
                registers = {}
                for _ in range(count):
                    reg = r.u8()
                    pname = r.zstr()
                    params.append(pname)
                    if reg:
                        registers[reg] = pname
                body = r.u16()
                # The preload flags claim low registers in a fixed order.
                claimed = 1
                for bit, preset in ((0x0001, "this"), (0x0004, "arguments"),
                                    (0x0010, "super"), (0x0040, "_root"),
                                    (0x0080, "_parent"), (0x0100, "_global")):
                    if flags & bit:
                        registers.setdefault(claimed, preset)
                        claimed += 1
                _ = register_count
            fn = Function(name, params, r.i, body, registers, flags)
            functions.append(fn)
            kind = "function" if action == 0x9B else "function2"
            text = f"Define{kind[0].upper()}{kind[1:]:<7s} {fn.signature()}  body={body}"
            lines.append((offset, text))
            # The body follows inline; keep walking so its actions are decoded
            # in place rather than skipped.
            continue
        elif action == 0x69:
            text = "Extends"

        r.i = end
        lines.append((offset, text))

    if pool_out is not None:
        pool_out[:] = pool
    return lines, functions, pool, targets


# --------------------------------------------------------------------------
# Stack reassembly
# --------------------------------------------------------------------------

# Binary operators, as (mnemonic, format). The stack order is second-pushed on
# top, so `a OP b` pops b then a.
BINARY = {
    "Add": "+", "Subtract": "-", "Multiply": "*", "Divide": "/", "Modulo": "%",
    "Add2": "+", "Less": "<", "Less2": "<", "Greater": ">", "StringLess": "lt",
    "StringGreater": "gt", "Equals": "==", "Equals2": "==", "StrictEquals": "===",
    "And": "&&", "Or": "||", "BitAnd": "&", "BitOr": "|", "BitXor": "^",
    "BitLShift": "<<", "BitRShift": ">>", "BitURShift": ">>>",
    "StringAdd": "+", "StringEquals": "==", "InstanceOf": "instanceof",
}

UNARY = {
    "Not": "!", "ToInteger": "int", "ToNumber": "Number", "ToString": "String",
    "TypeOf": "typeof", "Increment": "++", "Decrement": "--",
    "RandomNumber": "random", "StringLength": "len", "CharToAscii": "ord",
    "AsciiToChar": "chr", "MBStringLength": "mblen",
}


class Pseudo:
    """Turns one function body's actions back into expressions.

    Deliberately not a decompiler. It reassembles expressions, which is where all
    the unreadability is, and leaves control flow as labelled gotos.
    """

    def __init__(self, registers: dict[int, str]):
        self.stack: list[str] = []
        self.out: list[tuple[int, str]] = []
        self.registers = dict(registers)
        self.last_stored: str | None = None

    def reg(self, index: int) -> str:
        return self.registers.get(index, f"r{index}")

    def push(self, value: str) -> None:
        self.stack.append(value)

    def pop(self) -> str:
        # An underflow means the body reads a value pushed before it — the outer
        # scope's stack. Naming it is honest; inventing a value is not.
        return self.stack.pop() if self.stack else "<stack>"

    def emit(self, offset: int, text: str) -> None:
        self.out.append((offset, text))

    def step(self, offset: int, name: str, text: str, targets: set[int]) -> None:
        if name in BINARY:
            b, a = self.pop(), self.pop()
            self.push(f"({a} {BINARY[name]} {b})")
            return

        if name in ("Not", "ToInteger", "ToNumber", "ToString", "TypeOf"):
            self.push(f"{UNARY[name]}({self.pop()})")
            return

        if name in ("Increment", "Decrement"):
            self.push(f"({self.pop()} {'+' if name == 'Increment' else '-'} 1)")
            return

        if name in UNARY:
            self.push(f"{UNARY[name]}({self.pop()})")
            return

        handler = getattr(self, "op_" + name, None)
        if handler is not None:
            handler(offset, text, targets)
            return

        # Anything not modelled is shown as-is with the stack left alone, so a
        # gap is visible rather than silently corrupting everything after it.
        self.emit(offset, f"; {text}")

    # -- individual operations ---------------------------------------------

    def op_Push(self, offset, text, targets):
        for token in _split_operands(text[len("Push"):].strip()):
            self.push(token)

    def op_Pop(self, offset, text, targets):
        value = self.pop()
        # A discarded literal is dead stack, and the value a StoreRegister just
        # kept has already been printed as the assignment — re-emitting it makes
        # every `var x = expr` appear twice. Only a genuinely discarded call is a
        # statement worth a line.
        if "(" in value and value != self.last_stored:
            self.emit(offset, value)
        self.last_stored = None

    def op_GetMember(self, offset, text, targets):
        name, obj = self.pop(), self.pop()
        if name.startswith('"') and name.endswith('"') and name[1:-1].isidentifier():
            self.push(f"{obj}.{name[1:-1]}")
        else:
            self.push(f"{obj}[{name}]")

    def op_SetMember(self, offset, text, targets):
        value, name, obj = self.pop(), self.pop(), self.pop()
        target = (f"{obj}.{name[1:-1]}"
                  if name.startswith('"') and name.endswith('"') and name[1:-1].isidentifier()
                  else f"{obj}[{name}]")
        self.emit(offset, f"{target} = {value}")

    def op_GetVariable(self, offset, text, targets):
        name = self.pop()
        self.push(name[1:-1] if name.startswith('"') else f"var[{name}]")

    def op_SetVariable(self, offset, text, targets):
        value, name = self.pop(), self.pop()
        self.emit(offset, f"{name[1:-1] if name.startswith('"') else name} = {value}")

    def op_DefineLocal(self, offset, text, targets):
        value, name = self.pop(), self.pop()
        self.emit(offset, f"var {name[1:-1] if name.startswith('"') else name} = {value}")

    def op_DefineLocal2(self, offset, text, targets):
        self.emit(offset, f"var {self.pop()}")

    def op_CallMethod(self, offset, text, targets):
        name, obj = self.pop(), self.pop()
        args = self._args()
        label = name[1:-1] if name.startswith('"') else f"[{name}]"
        # A getter emitted by the compiler for `obj.Prop`; showing the call is noise.
        if label.startswith("__get__") and not args:
            self.push(f"{obj}.{label[7:]}")
        elif label.startswith("__set__") and len(args) == 1:
            self.emit(offset, f"{obj}.{label[7:]} = {args[0]}")
            self.push("undefined")
        else:
            self.push(f"{obj}.{label}({', '.join(args)})")

    def op_CallFunction(self, offset, text, targets):
        name = self.pop()
        self.push(f"{name[1:-1] if name.startswith('"') else name}({', '.join(self._args())})")

    def op_NewObject(self, offset, text, targets):
        name = self.pop()
        self.push(f"new {name[1:-1] if name.startswith('"') else name}"
                  f"({', '.join(self._args())})")

    def op_NewMethod(self, offset, text, targets):
        name, obj = self.pop(), self.pop()
        self.push(f"new {obj}.{name[1:-1] if name.startswith('"') else name}"
                  f"({', '.join(self._args())})")

    def op_InitArray(self, offset, text, targets):
        self.push(f"[{', '.join(self._args())}]")

    def op_InitObject(self, offset, text, targets):
        count = self._count()
        pairs = []
        for _ in range(count):
            value, key = self.pop(), self.pop()
            pairs.append(f"{key}: {value}")
        self.push("{" + ", ".join(reversed(pairs)) + "}")

    def op_PushDuplicate(self, offset, text, targets):
        top = self.stack[-1] if self.stack else "<stack>"
        self.push(top)

    def op_StackSwap(self, offset, text, targets):
        if len(self.stack) >= 2:
            self.stack[-1], self.stack[-2] = self.stack[-2], self.stack[-1]

    def op_Return(self, offset, text, targets):
        self.emit(offset, f"return {self.pop()}")

    def op_Throw(self, offset, text, targets):
        self.emit(offset, f"throw {self.pop()}")

    def op_Trace(self, offset, text, targets):
        self.emit(offset, f"trace({self.pop()})")

    def op_GetProperty(self, offset, text, targets):
        index, obj = self.pop(), self.pop()
        self.push(f"{obj}.{_property(index)}")

    def op_SetProperty(self, offset, text, targets):
        value, index, obj = self.pop(), self.pop(), self.pop()
        self.emit(offset, f"{obj}.{_property(index)} = {value}")

    def op_Delete(self, offset, text, targets):
        name, obj = self.pop(), self.pop()
        self.emit(offset, f"delete {obj}.{name}")

    def op_Delete2(self, offset, text, targets):
        self.emit(offset, f"delete {self.pop()}")

    def op_Extends(self, offset, text, targets):
        base, derived = self.pop(), self.pop()
        self.emit(offset, f"{derived} extends {base}")

    def op_CastOp(self, offset, text, targets):
        value, kind = self.pop(), self.pop()
        self.push(f"({kind}){value}")

    def op_Enumerate2(self, offset, text, targets):
        self.push(f"enumerate({self.pop()})")

    def _count(self) -> int:
        raw = self.pop()
        try:
            return int(float(raw))
        except ValueError:
            return 0

    def _args(self) -> list[str]:
        return [self.pop() for _ in range(self._count())]


def _property(index: str) -> str:
    try:
        return PROPERTIES.get(int(float(index)), f"property{index}")
    except ValueError:
        return f"property[{index}]"


def _split_operands(text: str) -> list[str]:
    """Split a Push operand list on commas that are not inside a string."""
    out, current, quoted, escaped = [], "", False, False
    for ch in text:
        if escaped:
            current += ch
            escaped = False
            continue
        if ch == "\\":
            current += ch
            escaped = True
            continue
        if ch == '"':
            quoted = not quoted
        if ch == "," and not quoted:
            out.append(current.strip())
            current = ""
            continue
        current += ch
    if current.strip():
        out.append(current.strip())
    return out


def print_pseudo(lines, fn, name, targets) -> None:
    """Print one function as expressions rather than stack operations."""
    body = [(offset, text) for offset, text in lines if fn.start <= offset < fn.end]
    machine = Pseudo(fn.registers)

    for offset, text in body:
        mnemonic = text.split()[0] if text else ""
        if mnemonic in ("Jump", "If"):
            target = text.split("->")[1].strip()
            if mnemonic == "If":
                machine.emit(offset, f"if ({machine.pop()}) goto {target}")
            else:
                machine.emit(offset, f"goto {target}")
            continue
        if mnemonic == "StoreRegister":
            index = int(text.split("r")[-1])
            top = machine.stack[-1] if machine.stack else "<stack>"
            machine.emit(offset, f"{machine.reg(index)} = {top}")
            machine.last_stored = top
            continue
        if mnemonic == "ConstantPool":
            continue
        if mnemonic.startswith("DefineFunction") and "body=" in text:
            machine.emit(offset, f"; nested {text}")
            continue
        machine.step(offset, mnemonic, text, targets)

    print(f"\n  --- {name}({', '.join(fn.params)})   {fn.size} bytes")
    if fn.registers:
        print(f"      registers: "
              + ", ".join(f"r{i}={n}" for i, n in sorted(fn.registers.items())))
    for offset, text in machine.out:
        mark = ">" if offset in targets else " "
        print(f"  {mark}{offset:#08x}  {text}")
    if machine.stack:
        print(f"      ; left on stack: {machine.stack}")


# --------------------------------------------------------------------------
# Presentation
# --------------------------------------------------------------------------


def blocks_of(body: bytes):
    """Yield (label, action bytes) for every action-carrying tag."""
    initial = 0
    for code, payload in tag_stream(body):
        if code == 59:  # DoInitAction
            sprite = struct.unpack_from("<H", payload, 0)[0]
            yield f"DoInitAction sprite {sprite}", payload[2:]
        elif code == 12:  # DoAction
            yield f"DoAction {initial}", payload
            initial += 1


def class_name_of(pool: list[str]) -> str:
    """The class a DoInitAction block defines.

    The compiler emits `_global.Name = function...`, so the pool opens with
    `_global` and the class name. A block that does not follow that shape is
    named for its first non-empty entry instead of guessed at.
    """
    if len(pool) >= 2 and pool[0] == "_global":
        return pool[1]
    for entry in pool:
        if entry:
            return entry
    return "<unnamed>"


def method_names(lines, functions) -> list[str]:
    """Method names in definition order, taking the pushed name for anonymous ones."""
    by_offset = {fn.start: fn for fn in functions}
    names: list[str] = []
    recent: list[str] = []
    for offset, text in lines:
        if text.startswith("Push"):
            for token in text[4:].strip().split(", "):
                if token.startswith('"'):
                    recent.append(token[1:-1])
        elif text.startswith("DefineFunction"):
            fn = None
            for start, candidate in by_offset.items():
                if candidate.signature() in text and start > offset:
                    fn = candidate
                    break
            name = fn.name if fn and fn.name else (recent[-1] if recent else "?")
            names.append(name)
            recent.clear()
    return names


def print_block(label: str, code: bytes, want_method: str | None,
                pseudo: bool = False) -> None:
    lines, functions, pool, targets = disassemble(code)
    print(f"===== {label}  class {class_name_of(pool)}  "
          f"{len(code)} bytes, {len(functions)} function(s)")

    # A method's body is the range [start, end); the name is whatever was pushed
    # immediately before the definition, because prototype methods are anonymous.
    named: list[tuple[Function, str]] = []
    recent: list[str] = []
    index = 0
    for offset, text in lines:
        if text.startswith("Push"):
            for token in text[4:].strip().split(", "):
                if token.startswith('"'):
                    recent.append(token[1:-1])
        elif text.startswith("DefineFunction") and index < len(functions):
            fn = functions[index]
            index += 1
            named.append((fn, fn.name or (recent[-1] if recent else "?")))
            recent.clear()

    if want_method:
        matches = [(fn, n) for fn, n in named if n.lower() == want_method.lower()]
        if not matches:
            print(f"  no method named {want_method}; have: "
                  + ", ".join(n for _, n in named))
            return
        for fn, name in matches:
            if pseudo:
                print_pseudo(lines, fn, name, targets)
                continue
            print(f"\n  --- {name}({', '.join(fn.params)})  "
                  f"registers {fn.registers}  {fn.size} bytes")
            for offset, text in lines:
                if fn.start <= offset < fn.end:
                    mark = ">" if offset in targets else " "
                    print(f"  {mark}{offset:#08x}  {text}")
        return

    depth_ends: list[int] = []
    name_by_start = {fn.start: n for fn, n in named}
    for offset, text in lines:
        while depth_ends and offset >= depth_ends[-1]:
            depth_ends.pop()
        indent = "  " * (len(depth_ends) + 1)
        mark = ">" if offset in targets else " "
        print(f"{mark}{offset:#08x}{indent}{text}")
        for fn, name in named:
            if fn.start == offset + 0 or (text.startswith("DefineFunction") and fn.start > offset
                                          and fn.signature() in text):
                if name_by_start.get(fn.start) == name:
                    depth_ends.append(fn.end)
                    print(f"{indent}  ; ^ {name}")
                break


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("swf")
    ap.add_argument("--class", dest="klass", help="only this class")
    ap.add_argument("--method", help="only this method (needs --class)")
    ap.add_argument("--pool", action="store_true", help="constant pools only")
    ap.add_argument("--raw", action="store_true", help="every block, undivided")
    ap.add_argument("--pseudo", action="store_true",
                    help="reassemble the stack into expressions (use with --method)")
    args = ap.parse_args()

    body = read_swf(args.swf)
    blocks = list(blocks_of(body))

    if not args.klass and not args.raw:
        for label, code in blocks:
            lines, functions, pool, _ = disassemble(code)
            name = class_name_of(pool)
            if args.pool:
                print(f"===== {label}  class {name}")
                print("  " + ", ".join(pool))
                print()
                continue
            names = method_names(lines, functions)
            seen = list(dict.fromkeys(names))
            print(f"{name:28s} {len(code):7d} bytes  {len(seen):3d} members  ({label})")
            print("    " + ", ".join(seen))
        return 0

    for label, code in blocks:
        _, _, pool, _ = disassemble(code)
        if args.klass and class_name_of(pool).lower() != args.klass.lower():
            continue
        print_block(label, code, args.method, args.pseudo)
    return 0


if __name__ == "__main__":
    sys.exit(main())
