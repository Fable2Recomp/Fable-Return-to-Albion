"""Bulk guest memory, gdb-style: dump, search, watch. These do NOT use the debug channel — they
read the arena from outside the process, so they work on a FROZEN run and (on Linux) on a dead
one. Every reply names its source and warns when it is post-mortem."""
import os
import struct
import time

from .. import arena, symbols
from ..registry import tool

DUMPS = os.environ.get(
    "REFII_MCP_DUMPS", os.path.join(os.path.expanduser("~"), ".cache", "refii-scratch",
                                    "mcp-dumps")
)

INLINE_LIMIT = 2048
FILE_LIMIT = 64 * 1024 * 1024


def _parse_addr(text):
    try:
        return int(str(text).lower().removeprefix("0x"), 16)
    except ValueError:
        return None


def _hexdump(data, base):
    lines = []
    for row in range(0, len(data), 16):
        chunk = data[row:row + 16]
        words = " ".join(
            chunk[i:i + 4].hex().upper().ljust(8) for i in range(0, len(chunk), 4))
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        lines.append(f"{base + row:08X}  {words}  {text}")
    return lines


@tool(
    "refii_dump",
    "Dump guest memory from OUTSIDE the process — needs no debug channel, so it works on a "
    "FROZEN run, and on Linux on a dead run's leaked arena (the reply says which source "
    "answered; post-mortem reads carry a warning). Small ranges come back as a big-endian "
    "hexdump; anything larger is written to a file whose path is returned, for local "
    "parsing. Bytes not yet touched by the guest read as zeros.",
    {
        "addr": {"type": "string", "description": "Guest address, hex."},
        "bytes": {"type": "integer",
                  "description": f"How many (default 256; > {INLINE_LIMIT} goes to a file; "
                                 f"max {FILE_LIMIT})."},
        "name": {"type": "string", "description": "Optional label for the file name."},
    },
    ["addr"],
)
def dump(args):
    addr = _parse_addr(args.get("addr"))
    if addr is None:
        return {"ok": False, "error": f"addr {args.get('addr')!r} is not hex"}
    count = max(1, min(int(args.get("bytes", 256)), FILE_LIMIT))

    reader, description, error = arena.find_reader()
    if reader is None:
        return {"ok": False, "error": error}
    try:
        data = reader.read(addr, count)
    finally:
        reader.close()
    if not data:
        return {"ok": False, "error": f"{addr:08X} is outside every guest view",
                "arena": description}

    reply = {"ok": True, "addr": f"{addr:08X}", "bytes": len(data), "arena": description}
    if len(data) <= INLINE_LIMIT:
        reply["dump"] = _hexdump(data, addr)
    else:
        os.makedirs(DUMPS, exist_ok=True)
        label = "".join(c if c.isalnum() else "-" for c in str(args.get("name", "dump")))
        path = os.path.join(
            DUMPS, f"{time.strftime('%H%M%S')}-{label}-{addr:08X}-{len(data):X}.bin")
        with open(path, "wb") as out:
            out.write(data)
        reply["path"] = path
        reply["preview"] = _hexdump(data[:256], addr)
        reply["note"] = "big-endian guest bytes, exactly as guest memory holds them"
    return reply


@tool(
    "refii_find",
    "Search guest memory for bytes — gdb's `find`, from outside the process. Give exactly "
    "one of `hex` (byte pattern, e.g. '3F800000'), `text` (ASCII), or `word` (an integer, "
    "searched as one big-endian 32-bit value). Scans only RESIDENT memory (sparse holes and "
    "uncommitted pages are skipped), covering each distinct byte once: physical-memory hits "
    "are reported at their 0xA0000000 window; the same bytes alias at 0x7F/0xC0/0xE0. "
    "Optional start/end narrow the range.",
    {
        "hex": {"type": "string", "description": "Byte pattern in hex."},
        "text": {"type": "string", "description": "ASCII pattern."},
        "word": {"type": "integer", "description": "One 32-bit value, matched big-endian."},
        "start": {"type": "string", "description": "Range start, hex (default all)."},
        "end": {"type": "string", "description": "Range end, hex (default all)."},
        "limit": {"type": "integer", "description": "Max hits (default 64)."},
    },
)
def find(args):
    given = [key for key in ("hex", "text", "word") if args.get(key) is not None]
    if len(given) != 1:
        return {"ok": False, "error": "give exactly one of hex, text, word"}
    if given[0] == "hex":
        try:
            pattern = bytes.fromhex(str(args["hex"]).replace(" ", ""))
        except ValueError:
            return {"ok": False, "error": f"{args['hex']!r} is not a hex byte string"}
    elif given[0] == "text":
        pattern = str(args["text"]).encode()
    else:
        pattern = struct.pack(">I", int(args["word"]) & 0xFFFFFFFF)
    if not pattern:
        return {"ok": False, "error": "empty pattern"}

    lo = _parse_addr(args.get("start", "0")) or 0
    hi = _parse_addr(args.get("end", "100000000"))
    if hi is None:
        hi = 0x100000000
    limit = max(1, min(int(args.get("limit", 64)), 1024))

    reader, description, error = arena.find_reader()
    if reader is None:
        return {"ok": False, "error": error}

    hits = []
    scanned = 0
    chunk_size = 4 * 1024 * 1024
    overlap = len(pattern) - 1
    try:
        for view_lo, view_hi in arena.SEARCH_VIEWS:
            span_lo, span_hi = max(lo, view_lo), min(hi, view_hi + 1)
            if span_lo >= span_hi:
                continue
            for run_addr, run_length in reader.data_runs(span_lo, span_hi):
                position = run_addr
                run_end = run_addr + run_length
                tail = b""
                while position < run_end and len(hits) < limit:
                    step = min(chunk_size, run_end - position)
                    data = reader.read(position, step)
                    scanned += len(data)
                    haystack = tail + data
                    offset = 0
                    while len(hits) < limit:
                        found = haystack.find(pattern, offset)
                        if found < 0:
                            break
                        hits.append(f"{position - len(tail) + found:08X}")
                        offset = found + 1
                    tail = data[-overlap:] if overlap else b""
                    position += step
                if len(hits) >= limit:
                    break
            if len(hits) >= limit:
                break
    finally:
        reader.close()

    return {
        "ok": True,
        "pattern": pattern.hex().upper(),
        "hits": symbols.annotate(hits) if symbols.loaded() else hits,
        "truncated": len(hits) >= limit,
        "scannedBytes": scanned,
        "arena": description,
    }


@tool(
    "refii_watch",
    "Poll one guest address from OUTSIDE the process and report every change with a "
    "timestamp — a zero-overhead software watchpoint. It cannot name the writer (that "
    "needs hardware watchpoints), but the change timing plus refii_threads usually can. "
    "Blocks for `duration` seconds; works on a frozen run (where 'no changes' is itself "
    "the answer).",
    {
        "addr": {"type": "string", "description": "Guest address, hex."},
        "width": {"type": "integer", "description": "Bytes to compare: 1, 2, 4 or 8 (default 4)."},
        "duration": {"type": "number", "description": "Seconds to watch (default 5, max 60)."},
        "interval_ms": {"type": "number", "description": "Poll interval (default 5ms, min 1)."},
        "suspects": {"type": "boolean",
                     "description": "On each change, snapshot the guest threads' lr in the "
                                    "same instant (needs the channel) — racy, but often "
                                    "names the writer's neighborhood."},
    },
    ["addr"],
)
def watch(args):
    addr = _parse_addr(args.get("addr"))
    if addr is None:
        return {"ok": False, "error": f"addr {args.get('addr')!r} is not hex"}
    width = int(args.get("width", 4))
    if width not in (1, 2, 4, 8):
        return {"ok": False, "error": "width must be 1, 2, 4 or 8"}
    duration = max(0.1, min(float(args.get("duration", 5)), 60.0))
    interval = max(0.001, float(args.get("interval_ms", 5)) / 1000.0)

    reader, description, error = arena.find_reader()
    if reader is None:
        return {"ok": False, "error": error}

    changes = []
    samples = 0
    try:
        last = reader.read(addr, width)
        began = time.monotonic()
        deadline = began + duration
        while time.monotonic() < deadline and len(changes) < 200:
            time.sleep(interval)
            value = reader.read(addr, width)
            samples += 1
            if value != last:
                change = {
                    "at": round(time.monotonic() - began, 4),
                    "from": last.hex().upper(),
                    "to": value.hex().upper(),
                }
                if args.get("suspects"):
                    from ..channel import ask
                    grabbed = ask({"op": "threads", "bt": False})
                    if grabbed.get("ok"):
                        suspects = [f"{t['tid']}:{t['lr']}"
                                    for t in grabbed.get("threads", []) if t.get("guest")]
                        change["suspects"] = symbols.annotate(
                            [s.split(":", 1)[1] for s in suspects]) \
                            if symbols.loaded() else suspects
                changes.append(change)
                last = value
        final = last
    finally:
        reader.close()

    return {
        "ok": True,
        "addr": f"{addr:08X}",
        "width": width,
        "value": final.hex().upper(),
        "changes": changes,
        "samples": samples,
        "watched": duration,
        "arena": description,
    }
