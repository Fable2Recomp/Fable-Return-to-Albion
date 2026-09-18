"""The host's own cost, Tracy captures, and the per-frame CSV — the render-phase discipline
(every number names its phase) with the collection automated."""
import csv
import glob
import os
import shutil
import subprocess
import time

from ..channel import SHOTS, ask
from ..registry import tool


@tool(
    "refii_metrics",
    "Godot's own frame cost — FPS, process/physics ms, draw calls, primitives, video "
    "memory, node count. The half the guest counters cannot see; refii_perf is the other "
    "half.",
)
def metrics(_args):
    return ask({"op": "metrics"})


@tool(
    "refii_fx",
    "The drawn LIVE particle effects nearest the eye, one line each: guest handle, the bank "
    "effect it resolved to (and its harvested name when known), distance, placement, the "
    "intensity the guest states for the instance, and what our simulation holds for it. "
    "Answers 'the trail is three blobs' with the effect's numbers instead of a screenshot.",
    {
        "count": {"type": "integer", "description": "How many effects (default 16, max 128)."},
    },
)
def fx(args):
    request = {"op": "fx"}
    if args.get("count") is not None:
        request["count"] = int(args["count"])
    return ask(request)


@tool(
    "refii_fxlayer",
    "The additive particle layer, measured live: whether it is up and resolvable, whether "
    "anything drew on it this frame, its pixel statistics (mean of the per-pixel max channel, "
    "peak, lit-pixel count), and its gain — which `gain` changes on the spot, so a faint "
    "trail can be dialled by eye without a rebuild. `dump` saves the layer as a PNG under "
    "~/.cache/refii-scratch/fxlayer.",
    {
        "gain": {"type": "number", "description": "New gain for the layer (0..64)."},
        "dump": {"type": "boolean", "description": "Also write the layer image to scratch."},
    },
)
def fxlayer(args):
    request = {"op": "fxlayer"}
    if args.get("gain") is not None:
        request["gain"] = float(args["gain"])
    if args.get("dump"):
        request["dump"] = True
    return ask(request)


@tool(
    "refii_mark",
    "Drop a marker: a [mark] line into the game's log and a message onto the Tracy "
    "timeline, so 'pressed A here' lines up with the surrounding traffic when either is "
    "read later.",
    {
        "text": {"type": "string", "description": "The marker text."},
    },
    ["text"],
)
def mark(args):
    return ask({"op": "mark", "text": str(args.get("text", ""))})


def _tracy_tool(name):
    for candidate in (
        os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                     "..", "bin", name),
        shutil.which(name) or "",
        shutil.which(f"tracy-{name}") or "",
    ):
        if candidate and os.path.isfile(candidate):
            return candidate
    return None


@tool(
    "refii_trace",
    "Capture N seconds of the live Tracy stream headlessly and return the top zones by "
    "self-time — where the frame actually goes, guest threads and Godot phases on one "
    "timeline. The .tracy file is kept for the GUI. Needs the vendored capture tools built "
    "once (tools/bin/capture + csvexport, from rexglue-net/rexglue-sdk/thirdparty/tracy — "
    "the error says how).",
    {
        "seconds": {"type": "integer", "description": "Capture length (default 10, max 120)."},
        "top": {"type": "integer", "description": "Zones to report (default 20)."},
    },
)
def trace(args):
    capture = _tracy_tool("capture")
    csvexport = _tracy_tool("csvexport")
    if capture is None or csvexport is None:
        return {"ok": False, "error": (
            "tracy tools not found — build them once from the vendored tree "
            "(protocol-matched 0.13.1): cmake -B /tmp/tracy-cap "
            "rexglue-net/rexglue-sdk/thirdparty/tracy/capture && cmake --build /tmp/tracy-cap "
            "(same for csvexport), then put the binaries in tools/bin/")}

    seconds = max(1, min(int(args.get("seconds", 10)), 120))
    out_dir = os.path.join(os.path.dirname(SHOTS), "mcp-traces")
    os.makedirs(out_dir, exist_ok=True)
    trace_path = os.path.join(out_dir, f"{time.strftime('%H%M%S')}.tracy")
    grabbed = subprocess.run(
        [capture, "-a", "127.0.0.1", "-o", trace_path, "-s", str(seconds), "-f"],
        capture_output=True, text=True, timeout=seconds + 60)
    if grabbed.returncode != 0 or not os.path.exists(trace_path):
        return {"ok": False, "error": f"capture failed: {grabbed.stderr[-400:] or grabbed.stdout[-400:]}"}

    exported = subprocess.run(
        [csvexport, trace_path], capture_output=True, text=True, timeout=120)
    if exported.returncode != 0:
        return {"ok": True, "trace": trace_path,
                "note": f"captured, but csvexport failed: {exported.stderr[-300:]}"}

    zones = []
    for row in csv.DictReader(exported.stdout.splitlines()):
        try:
            zones.append({
                "zone": row.get("name", "?"),
                "counts": int(row.get("counts", 0)),
                "totalMs": round(int(row.get("total_ns", 0)) / 1e6, 2),
                "meanUs": round(float(row.get("mean_ns", 0)) / 1e3, 1),
            })
        except (ValueError, TypeError):
            continue
    zones.sort(key=lambda z: z["totalMs"], reverse=True)
    return {"ok": True, "trace": trace_path, "seconds": seconds,
            "topZones": zones[:max(1, min(int(args.get("top", 20)), 100))]}


@tool(
    "refii_perfcsv",
    "Summarize the per-frame CSV the `perf_log_csv` cvar writes (switch it on live with "
    "refii_cvar_set): per-counter mean and max over the last N frames. The render-phase "
    "rule, automated — sample while the phase of interest is on screen.",
    {
        "path": {"type": "string", "description": "CSV path (default: the cvar's current value)."},
        "frames": {"type": "integer", "description": "Last N frames (default 300)."},
    },
)
def perfcsv(args):
    path = str(args.get("path", ""))
    if not path:
        cvars = ask({"op": "cvars", "match": "perf_log_csv"})
        for entry in cvars.get("cvars", []) if cvars.get("ok") else []:
            if entry.get("name") == "perf_log_csv":
                path = entry.get("value", "")
        if not path:
            return {"ok": False, "error": "perf_log_csv is not set — refii_cvar_set it to a "
                                          "file path first (hot-reload, no rebuild)"}
    try:
        with open(path) as file:
            rows = list(csv.DictReader(file))
    except OSError as error:
        return {"ok": False, "error": str(error)}
    if not rows:
        return {"ok": False, "error": f"{path} has no frames yet"}

    take = rows[-max(1, int(args.get("frames", 300))):]
    summary = {}
    for name in take[0]:
        try:
            values = [float(row[name]) for row in take]
        except (ValueError, TypeError):
            continue
        summary[name] = {"mean": round(sum(values) / len(values), 2),
                         "max": round(max(values), 2)}
    return {"ok": True, "path": path, "frames": len(take), "counters": summary}


@tool(
    "refii_flicker",
    "Write the post-fx FLICKER TRAP's frame ring now: the last 48 rendered frames at quarter "
    "resolution, as PNGs under user://flicker, so a one-frame glitch that cannot be "
    "screenshotted by hand can be looked at frame by frame. The ring also writes itself "
    "when the scene's mean luminance jumps between consecutive frames. Armed at launch by "
    "the `postfx_flicker_trap` cvar (a ratio above 1; 2.5 is a good start). The write "
    "happens on the next rendered frame, so `lastDump` names the previous dump on this call "
    "and the new one on the next; the game log's `[postfx] flicker trap` line has the "
    "same path.",
)
def flicker(_args):
    return ask({"op": "flicker"})
