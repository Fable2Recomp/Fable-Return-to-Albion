"""Finding unknown state: snapshot/diff, and cheat-engine narrowing.

Both work on the ARENA (outside the process), so they run against live, frozen and (Linux)
dead runs, and a snapshot costs the game nothing. This pair is how bytes like the title's
armed flag get found in minutes instead of sessions: diff answers "what did that press
touch", narrow answers "which cell holds this value".
"""
import json
import os
import struct
import time

from .. import arena
from ..registry import tool

HUNTS = os.environ.get(
    "REFII_MCP_HUNTS", os.path.join(os.path.expanduser("~"), ".cache", "refii-scratch",
                                    "mcp-hunts")
)

SNAPSHOT_CAP = 768 * 1024 * 1024
CANDIDATE_CAP = 8_000_000
CHUNK = 4 * 1024 * 1024


def _regions_of(reader, spec):
    """(addr, length) list: explicit 'lo-hi' hex ranges, or every resident run."""
    if spec:
        out = []
        for part in str(spec).split(","):
            lo_text, _, hi_text = part.partition("-")
            try:
                lo = int(lo_text.strip().removeprefix("0x"), 16)
                hi = int(hi_text.strip().removeprefix("0x"), 16)
            except ValueError:
                return None
            for view_lo, view_hi in arena.SEARCH_VIEWS:
                span_lo, span_hi = max(lo, view_lo), min(hi, view_hi + 1)
                if span_lo < span_hi:
                    out.extend(reader.data_runs(span_lo, span_hi))
        return out
    out = []
    for view_lo, view_hi in arena.SEARCH_VIEWS:
        out.extend(reader.data_runs(view_lo, view_hi + 1))
    return out


def _parse_value(text, width, as_float):
    if as_float:
        try:
            return struct.pack(">f" if width == 4 else ">d", float(text))
        except (ValueError, struct.error):
            return None
    try:
        number = int(str(text), 0)
    except (ValueError, TypeError):
        return None
    try:
        return number.to_bytes(width, "big", signed=number < 0)
    except OverflowError:
        return None


@tool(
    "refii_diff",
    "Find what changed in guest memory between two moments. op='snapshot' saves the resident "
    "memory (or explicit regions) under a name; op='compare' diffs a named snapshot against "
    "the LIVE arena (or a second snapshot via `against`) and reports changed words, adjacent "
    "changes merged into runs. The workflow for unknown state bytes: snapshot, press the "
    "button, compare. op='list' shows saved snapshots. Runs against frozen and post-mortem "
    "arenas too.",
    {
        "op": {"type": "string", "enum": ["snapshot", "compare", "list"]},
        "name": {"type": "string", "description": "Snapshot name (default 'snap')."},
        "against": {"type": "string",
                    "description": "compare: second snapshot name instead of live memory."},
        "regions": {"type": "string",
                    "description": "snapshot: 'lo-hi[,lo-hi...]' hex ranges (default: all "
                                   "resident memory)."},
        "limit": {"type": "integer", "description": "compare: max change runs (default 200)."},
    },
    ["op"],
)
def diff(args):
    op = args.get("op")
    name = "".join(c if c.isalnum() or c in "-_" else "-" for c in str(args.get("name", "snap")))
    os.makedirs(HUNTS, exist_ok=True)
    manifest_path = os.path.join(HUNTS, f"{name}.diff.json")
    data_path = os.path.join(HUNTS, f"{name}.diff.bin")

    if op == "list":
        snapshots = []
        for file in sorted(os.listdir(HUNTS)):
            if file.endswith(".diff.json"):
                try:
                    with open(os.path.join(HUNTS, file)) as handle:
                        meta = json.load(handle)
                    snapshots.append({
                        "name": file[:-10],
                        "taken": meta.get("taken"),
                        "bytes": meta.get("bytes"),
                        "regions": len(meta.get("runs", [])),
                    })
                except (OSError, ValueError):
                    continue
        return {"ok": True, "snapshots": snapshots, "dir": HUNTS}

    reader, description, error = arena.find_reader()
    if reader is None:
        return {"ok": False, "error": error}

    try:
        if op == "snapshot":
            regions = _regions_of(reader, args.get("regions", ""))
            if regions is None:
                return {"ok": False, "error": f"bad regions {args.get('regions')!r}"}
            runs = []
            total = 0
            with open(data_path, "wb") as out:
                for addr, length in regions:
                    if total + length > SNAPSHOT_CAP:
                        length = SNAPSHOT_CAP - total
                        if length <= 0:
                            break
                    position = 0
                    while position < length:
                        step = min(CHUNK, length - position)
                        out.write(reader.read(addr + position, step))
                        position += step
                    runs.append({"addr": addr, "length": length, "offset": total})
                    total += length
            meta = {"taken": time.strftime("%Y-%m-%dT%H:%M:%S"), "bytes": total,
                    "runs": runs, "arena": description}
            with open(manifest_path, "w") as out:
                json.dump(meta, out)
            return {"ok": True, "name": name, "bytes": total, "regions": len(runs),
                    "arena": description,
                    "truncated": total >= SNAPSHOT_CAP or None}

        if op == "compare":
            try:
                with open(manifest_path) as handle:
                    meta = json.load(handle)
            except (OSError, ValueError):
                return {"ok": False, "error": f"no snapshot named '{name}' — take one with "
                                              "op='snapshot' first"}
            against = args.get("against")
            other = None
            if against:
                against = "".join(c if c.isalnum() or c in "-_" else "-" for c in str(against))
                try:
                    with open(os.path.join(HUNTS, f"{against}.diff.json")) as handle:
                        other = json.load(handle)
                    other_data = open(os.path.join(HUNTS, f"{against}.diff.bin"), "rb")
                except (OSError, ValueError):
                    return {"ok": False, "error": f"no snapshot named '{against}'"}

            limit = max(1, min(int(args.get("limit", 200)), 2000))
            changes = []
            total_changed_words = 0
            compared = 0
            with open(data_path, "rb") as old_data:
                for run in meta["runs"]:
                    addr, length, offset = run["addr"], run["length"], run["offset"]
                    position = 0
                    while position < length:
                        step = min(CHUNK, length - position)
                        old_data.seek(offset + position)
                        before = old_data.read(step)
                        if other is None:
                            after = reader.read(addr + position, step)
                        else:
                            hit = next((r for r in other["runs"]
                                        if r["addr"] <= addr + position and
                                        addr + position + step <= r["addr"] + r["length"]), None)
                            if hit is None:
                                position += step
                                continue
                            other_data.seek(hit["offset"] + (addr + position - hit["addr"]))
                            after = other_data.read(step)
                        compared += step
                        if before != after:
                            words = min(len(before), len(after)) // 4
                            run_start = None
                            for w in range(words + 1):
                                same = w == words or (
                                    before[w * 4:w * 4 + 4] == after[w * 4:w * 4 + 4])
                                if not same and run_start is None:
                                    run_start = w
                                elif same and run_start is not None:
                                    total_changed_words += w - run_start
                                    if len(changes) < limit:
                                        at = addr + position + run_start * 4
                                        changes.append({
                                            "addr": f"{at:08X}",
                                            "words": w - run_start,
                                            "old": before[run_start * 4:
                                                          min(w, run_start + 4) * 4].hex().upper(),
                                            "new": after[run_start * 4:
                                                         min(w, run_start + 4) * 4].hex().upper(),
                                        })
                                    run_start = None
                        position += step
            if other is not None:
                other_data.close()
            return {"ok": True, "name": name,
                    "against": against or "live",
                    "comparedBytes": compared,
                    "changedWords": total_changed_words,
                    "changeRuns": changes,
                    "truncated": total_changed_words > 0 and len(changes) >= limit,
                    "arena": description,
                    "snapshotTaken": meta.get("taken")}

        return {"ok": False, "error": f"unknown op '{op}'"}
    finally:
        reader.close()


@tool(
    "refii_narrow",
    "Cheat-engine narrowing: find which cell holds a value by filtering candidates across "
    "state changes. op='start' scans resident memory (optionally range-limited) for cells "
    "equal to `value` at `width`; then change the state in game and op='filter' with kind = "
    "'eq' (new value), 'changed', 'unchanged', 'inc' or 'dec' — repeat until few remain. "
    "op='list' shows survivors with current values; op='reset' clears. `float=true` treats "
    "4-byte values as big-endian float. Values: decimal, 0x-hex, or float text.",
    {
        "op": {"type": "string", "enum": ["start", "filter", "list", "reset"]},
        "value": {"type": "string", "description": "start: sought value. filter kind='eq'/'ne': "
                                                   "the new value."},
        "width": {"type": "integer", "description": "Cell size 1/2/4/8 (default 4)."},
        "float": {"type": "boolean", "description": "Treat value as float (width 4 or 8)."},
        "kind": {"type": "string", "enum": ["eq", "ne", "changed", "unchanged", "inc", "dec"],
                 "description": "filter: how survivors are chosen against their last value."},
        "range": {"type": "string", "description": "start: 'lo-hi' hex range (default all)."},
        "session": {"type": "string", "description": "Hunt name (default 'narrow')."},
        "limit": {"type": "integer", "description": "list: max rows (default 40)."},
    },
    ["op"],
)
def narrow(args):
    op = args.get("op")
    session = "".join(
        c if c.isalnum() or c in "-_" else "-" for c in str(args.get("session", "narrow")))
    os.makedirs(HUNTS, exist_ok=True)
    meta_path = os.path.join(HUNTS, f"{session}.narrow.json")
    data_path = os.path.join(HUNTS, f"{session}.narrow.bin")

    if op == "reset":
        removed = False
        for path in (meta_path, data_path):
            if os.path.exists(path):
                os.remove(path)
                removed = True
        return {"ok": True, "session": session, "removed": removed}

    def load_meta():
        try:
            with open(meta_path) as handle:
                return json.load(handle)
        except (OSError, ValueError):
            return None

    def load_candidates(width):
        record = struct.Struct(f">I{width}s")
        out = []
        with open(data_path, "rb") as data:
            blob = data.read()
        for i in range(0, len(blob) - record.size + 1, record.size):
            addr, held = record.unpack_from(blob, i)
            out.append((addr, held))
        return out

    def save_candidates(candidates, width):
        record = struct.Struct(f">I{width}s")
        with open(data_path, "wb") as data:
            for addr, held in candidates:
                data.write(record.pack(addr, held))

    if op == "start":
        width = int(args.get("width", 4))
        if width not in (1, 2, 4, 8):
            return {"ok": False, "error": "width must be 1, 2, 4 or 8"}
        pattern = _parse_value(args.get("value"), width, bool(args.get("float")))
        if pattern is None:
            return {"ok": False, "error": f"value {args.get('value')!r} does not fit width "
                                          f"{width}"}
        reader, description, error = arena.find_reader()
        if reader is None:
            return {"ok": False, "error": error}
        candidates = []
        try:
            regions = _regions_of(reader, args.get("range", ""))
            if regions is None:
                return {"ok": False, "error": f"bad range {args.get('range')!r}"}
            for addr, length in regions:
                position = 0
                while position < length:
                    step = min(CHUNK, length - position)
                    data = reader.read(addr + position, step)
                    found = data.find(pattern)
                    while found >= 0:
                        at = addr + position + found
                        if at % width == 0:
                            candidates.append((at, pattern))
                            if len(candidates) > CANDIDATE_CAP:
                                return {"ok": False,
                                        "error": f"over {CANDIDATE_CAP} matches — narrow with "
                                                 "`range`, a wider width, or a rarer value"}
                        found = data.find(pattern, found + 1)
                    position += step
        finally:
            reader.close()
        save_candidates(candidates, width)
        with open(meta_path, "w") as out:
            json.dump({"width": width, "float": bool(args.get("float")),
                       "steps": [f"start eq {args.get('value')}"],
                       "count": len(candidates)}, out)
        return {"ok": True, "session": session, "candidates": len(candidates),
                "arena": description,
                "next": "change the state in game, then op='filter'"}

    meta = load_meta()
    if meta is None:
        return {"ok": False, "error": f"no hunt '{session}' — op='start' first"}
    width = meta["width"]

    if op in ("filter", "list"):
        reader, description, error = arena.find_reader()
        if reader is None:
            return {"ok": False, "error": error}
        try:
            candidates = load_candidates(width)
            # Batched current-value reads: one arena read covers every candidate within 64KB.
            current = {}
            window_start = None
            window_data = b""
            for addr, _ in candidates:
                if window_start is None or addr + width > window_start + len(window_data):
                    window_start = addr
                    window_data = reader.read(addr, 65536)
                current[addr] = window_data[addr - window_start:addr - window_start + width]
        finally:
            reader.close()

        if op == "list":
            limit = max(1, min(int(args.get("limit", 40)), 500))
            rows = [{"addr": f"{addr:08X}", "was": held.hex().upper(),
                     "now": current[addr].hex().upper()}
                    for addr, held in candidates[:limit]]
            return {"ok": True, "session": session, "candidates": len(candidates),
                    "steps": meta.get("steps", []), "rows": rows, "arena": description}

        kind = args.get("kind", "")
        want = None
        if kind in ("eq", "ne"):
            want = _parse_value(args.get("value"), width, meta.get("float"))
            if want is None:
                return {"ok": False, "error": f"kind '{kind}' needs a value fitting width "
                                              f"{width}"}
        elif kind not in ("changed", "unchanged", "inc", "dec"):
            return {"ok": False, "error": "kind must be eq, ne, changed, unchanged, inc or dec"}

        survivors = []
        for addr, held in candidates:
            now = current[addr]
            if len(now) < width:
                continue
            if kind == "eq":
                keep = now == want
            elif kind == "ne":
                keep = now != want
            elif kind == "changed":
                keep = now != held
            elif kind == "unchanged":
                keep = now == held
            else:
                before = int.from_bytes(held, "big")
                after = int.from_bytes(now, "big")
                keep = after > before if kind == "inc" else after < before
            if keep:
                survivors.append((addr, now))
        save_candidates(survivors, width)
        meta["steps"] = meta.get("steps", []) + [
            f"{kind} {args.get('value')}" if want is not None else kind]
        meta["count"] = len(survivors)
        with open(meta_path, "w") as out:
            json.dump(meta, out)
        reply = {"ok": True, "session": session, "candidates": len(survivors),
                 "steps": meta["steps"], "arena": description}
        if len(survivors) <= 20:
            reply["rows"] = [{"addr": f"{addr:08X}", "value": now.hex().upper()}
                             for addr, now in survivors]
        return reply

    return {"ok": False, "error": f"unknown op '{op}'"}
