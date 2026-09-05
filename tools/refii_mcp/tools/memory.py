"""Raw guest words, and the pointer chain that reaches them."""
import struct

from .. import arena, chainfile, symbols
from ..channel import ask
from ..registry import tool


def _words_at(addr, count):
    """Words via the channel, else via the ARENA — so peek keeps working on a frozen run,
    where the channel (served from the host's main thread) is exactly what stopped."""
    reply = ask({"op": "peek", "addr": f"{addr:08X}", "words": count})
    if reply.get("ok") and reply.get("words"):
        return [int(word, 16) for word in reply.get("words", [])], reply
    # Not ok, or ok with NOTHING — the game is up but the guest is not (boot, codegen), a
    # state in which the arena often already exists. Fall through to it.

    reader, description, _ = arena.find_reader()
    if reader is None:
        return None, reply
    try:
        data = reader.read(addr, count * 4)
    finally:
        reader.close()
    if len(data) < 4:
        return None, reply
    words = [struct.unpack_from(">I", data, i * 4)[0] for i in range(len(data) // 4)]
    return words, {
        "ok": True,
        "source": "arena",
        "arena": description,
        "channelError": reply.get("error"),
    }


def _decode(word):
    """One word the four ways it is ever meant: address, count, ratio, tag."""
    raw = struct.pack(">I", word)
    text = "".join(chr(b) if 32 <= b < 127 else "." for b in raw)
    decoded = {
        "hex": f"{word:08X}",
        "int": struct.unpack(">i", raw)[0],
        "float": round(struct.unpack(">f", raw)[0], 6),
        "ascii": text,
    }
    symbol = symbols.resolve(word)
    if symbol is not None:
        decoded["sym"] = symbol
    return decoded


@tool(
    "refii_peek",
    "Read guest words at an address, optionally walking a pointer chain to get there. "
    "The microscope: after IDA has said which field holds what, this reads it in the LIVE "
    "run without another native rebuild.\n\n"
    "`deref` is the chain: each offset is added to the current address and dereferenced, so "
    "`addr=83496AB8, deref=[0, 0x1C]` reads *(0x83496AB8) — the game root, which needs that "
    "deref — and then the word at root+0x1C. Every hop is reported with the address it was "
    "read FROM, because an offset quoted against the wrong base looks perfectly reasonable "
    "and has already reached running code in this project. The last hop's value is itself the "
    "answer when the field holds a value rather than a pointer.\n\n"
    "Each word is decoded as hex, signed int, big-endian float and four ASCII bytes. "
    "Read-only, loopback-only.",
    {
        "addr": {"type": "string", "description": "Guest address in hex, e.g. '83496AB8'."},
        "chain": {"type": "string",
                  "description": "A named chain from chains.toml instead of addr — its "
                                 "verified base+deref runs first, extra deref appends. "
                                 "refii_chains lists them."},
        "deref": {
            "type": "array",
            "items": {"type": "integer"},
            "description": "Offsets to walk. Each is added to the current address and the "
                           "word there is read. Omit to dump at `addr` directly.",
        },
        "words": {"type": "integer", "description": "Words to dump at the end (default 8, max 64)."},
    },
    ["addr"],
)
def peek(args):
    deref = list(args.get("deref") or [])
    note = None
    if args.get("chain"):
        preset = chainfile.get(str(args["chain"]))
        if preset is None:
            known = ", ".join(sorted(chainfile.all_chains()))
            return {"ok": False,
                    "error": f"no chain named {args['chain']!r} — chains.toml has: {known}"}
        base, chain_deref, note, fields = preset
        cursor = base
        deref = chain_deref + deref
    else:
        try:
            cursor = int(str(args.get("addr", "0")).removeprefix("0x"), 16)
        except ValueError:
            return {"ok": False, "error": f"addr {args.get('addr')!r} is not hex"}

    hops = []
    for offset in deref:
        offset = int(offset)
        at = (cursor + offset) & 0xFFFFFFFF
        got, reply = _words_at(at, 1)
        if got is None:
            return reply
        if not got:
            return {"ok": False, "error": f"{at:08X} is not readable guest memory",
                    "hops": hops}
        hops.append({"read": f"{at:08X}", "base": f"{cursor:08X}", "offset": f"+0x{offset:X}",
                     "value": f"{got[0]:08X}"})
        cursor = got[0]

    count = max(1, min(int(args.get("words", 8)), 64))
    got, reply = _words_at(cursor, count)
    if got is None:
        return reply

    dump = []
    for index, word in enumerate(got):
        entry = {"at": f"{(cursor + index * 4) & 0xFFFFFFFF:08X}", "offset": f"+0x{index * 4:X}"}
        entry.update(_decode(word))
        dump.append(entry)

    reply["addr"] = f"{cursor:08X}"
    reply["hops"] = hops
    if note:
        reply["chainNote"] = note
    if args.get("chain") and fields:
        decoded_fields = []
        for field in fields:
            try:
                off = int(field.get("off", 0))
                kind = str(field.get("type", "u32"))
                width = {"u8": 1, "u16": 2}.get(kind, 4)
                raw, _ = _words_at((cursor + off) & ~3, 1)
                if raw is None:
                    continue
                word = raw[0]
                shift = (3 - ((cursor + off) % 4) - (width - 1)) * 8 if width < 4 else 0
                value = (word >> shift) & ((1 << (width * 8)) - 1) if width < 4 else word
                entry = {"name": str(field.get("name", f"+0x{off:X}")),
                         "at": f"{(cursor + off) & 0xFFFFFFFF:08X}", "hex": f"{value:0{width*2}X}"}
                if kind == "s32":
                    entry["value"] = struct.unpack(">i", struct.pack(">I", word))[0]
                elif kind == "f32":
                    entry["value"] = round(struct.unpack(">f", struct.pack(">I", word))[0], 6)
                elif kind == "ptr":
                    entry["sym"] = symbols.resolve(word)
                else:
                    entry["value"] = value
                decoded_fields.append(entry)
            except (ValueError, TypeError):
                continue
        reply["fields"] = decoded_fields
    reply["words"] = dump
    if len(got) < count:
        reply["truncated"] = f"only {len(got)} of {count} words were readable"
    return reply
