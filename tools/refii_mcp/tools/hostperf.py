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


def _motion_summary(rows):
    """Frame-to-frame motion of a drawn-position trace: what a viewer sees as judder.

    A backward step is one whose direction opposes the step before it — the drawn point went
    back on itself. Speeds are metres per second between consecutive frames; their spread is the
    unevenness of motion the guest stated as smooth.
    """
    steps = []
    for before, after in zip(rows, rows[1:]):
        dt = (after["usec"] - before["usec"]) / 1e6
        if dt <= 0:
            continue
        delta = [a - b for a, b in zip(after["pos"], before["pos"])]
        steps.append((delta, dt))
    moving = [(d, dt) for d, dt in steps if sum(c * c for c in d) > 1e-10]
    if len(moving) < 3:
        return {"frames": len(rows), "moving": len(moving),
                "note": "the camera barely moved — pan or walk while the trace runs"}
    backward = 0
    for (d0, _), (d1, _) in zip(moving, moving[1:]):
        if sum(a * b for a, b in zip(d0, d1)) < 0:
            backward += 1
    speeds = [sum(c * c for c in d) ** 0.5 / dt for d, dt in moving]
    mean = sum(speeds) / len(speeds)
    spread = (sum((v - mean) ** 2 for v in speeds) / len(speeds)) ** 0.5
    return {
        "frames": len(rows),
        "moving": len(moving),
        "backwardSteps": backward,
        "meanSpeed": round(mean, 4),
        "speedSpreadPercent": round(100.0 * spread / mean, 1) if mean > 0 else None,
        "slowestOverMean": round(min(speeds) / mean, 3) if mean > 0 else None,
        "fastestOverMean": round(max(speeds) / mean, 3) if mean > 0 else None,
    }


@tool(
    "refii_camera",
    "The drawn camera's smoothness, measured. Call with `trace` = a frame count to ARM a "
    "per-frame recording of the drawn camera position, move the camera in the game while it "
    "runs, then call again with no arguments to read it back summarised: backward steps (the "
    "drawn point reversing on itself — visible judder), the spread of frame-to-frame speed, "
    "and the playhead's swap health (`lastSwapAlpha` 1.0 = the pair landed on time, "
    "`worstSwapError` = furthest from it, `restarts` = discontinuities). `raw` returns the "
    "rows as well. Reads only; arming a trace changes nothing on screen.",
    {
        "trace": {"type": "integer", "description": "Arm a recording of this many frames."},
        "raw": {"type": "boolean", "description": "Include the per-frame rows (default false)."},
    },
)
def camera(args):
    if args.get("trace") is not None:
        return ask({"op": "camera", "trace": max(1, min(int(args["trace"]), 20000))})
    reply = ask({"op": "camera"})
    if not reply.get("ok", False):
        return reply
    rows = reply.get("trace") or []
    reply["motion"] = _motion_summary(rows)
    if not args.get("raw"):
        reply.pop("trace", None)
    return reply


_RENDER_LEVERS = {
    "sunShadow": {"type": ["boolean", "null"],
                  "description": "Force the sun's shadow on/off; null hands it back to the level."},
    "sunSplits": {"type": ["integer", "null"],
                  "description": "Force the sun's cascade count; null hands it back."},
    "entities": {"type": "boolean", "description": "Show or hide every guest-driven entity."},
    "reflections": {"type": "boolean", "description": "Water reflection viewports on/off."},
    "reflectionCadence": {"type": "integer", "description": "Frames between reflection updates."},
    "hud": {"type": "boolean", "description": "The HUD view on/off."},
    "fx": {"type": "boolean", "description": "The displacement FX layer on/off."},
    "vsync": {"type": "string", "enum": ["off", "on", "adaptive", "mailbox"],
              "description": "Present mode. Under any vsync mode fps measures the display."},
    "maxFps": {"type": "integer", "description": "Frame cap; 0 is uncapped."},
    "shadowBudget": {"type": "integer", "description": "Placed lights allowed to cast shadows."},
    "lightDebug": {"type": "integer",
                   "description": "Shader view of ONE lighting term: 1 bake, 2 direct, 3 raw albedo, "
                                  "4 normal-map darken, 5 bake mode, 8 view normal, 9 light on white, "
                                  "10 facing (green front / red back); "
                                  "0 off. SET IT BACK TO 0."},
    "gi": {"type": "boolean", "description": "Godot's dynamic GI (SDFGI) on/off, to read its cost as a difference."},
    "fillFloor": {"type": "number",
                  "description": "Share of the mean authored bake kept as a flat unoccluded floor (default 0.2); the "
                                 "rest is the ambient the GI traces. 0 is the floor off."},
    "giHalf": {"type": "boolean",
               "description": "Resolve the GI at half the frame's resolution (the default) or at every pixel."},
    "shadowParaboloid": {"type": "boolean",
                         "description": "Fixture omni shadow maps as two paraboloids instead of six cube faces."},
    "entityBatches": {"type": "boolean",
                      "description": "Guest props unchanged for two seconds are drawn from batches, their nodes hidden; "
                                     "false returns every one to its node on the next walk. Read entityBatched."},
    "propShadow": {"type": "boolean",
                   "description": "Prop batches cast through a merged cast-only twin; false hands each batch its own "
                                  "shadow back at once. Single-node props are not switched by it."},
    "shadowProxy": {"type": "boolean",
                    "description": "Characters cast through one merged cast-only mesh; false hands each body its "
                                   "own shadow back within a reconcile pass (watch shadowProxies fall to 0)."},
    "exposureLock": {"type": ["number", "null"],
                     "description": "Pin the exposure gain for like-for-like shots; null hands it back."},
    "placedShadowBlur": {"type": "number",
                         "description": "Every placed light's ShadowBlur; Godot's default is 1."},
    "placedShadowBias": {"type": ["number", "null"],
                         "description": "Every placed light's ShadowBias; null is the per-type "
                                        "default (omni 0.1, spot 0.03)."},
    "placedShadowNormalBias": {"type": "number",
                               "description": "Every placed light's ShadowNormalBias; default 1."},
    "positionalFilter": {"type": "integer",
                         "description": "Omni/spot soft shadow filter, 0 hard .. 5 ultra; "
                                        "project default 2."},
    "positionalAtlas": {"type": "integer",
                        "description": "Omni/spot shadow atlas edge in texels; default 4096."},
    "lightBudget": {"type": "integer", "description": "Placed lights kept lit; 0 is all."},
    "lightRangeScale": {"type": "number", "description": "Every placed light's reach, scaled."},
    "poseFullRate": {"type": "number",
                     "description": "Radius in metres inside which rigs pose every step."},
    "feedEveryFrame": {"type": "boolean", "description": "Walk the entity feed every frame."},
}


@tool(
    "refii_render",
    "The live render levers, for bisecting a frame's cost: with no arguments it only REPORTS "
    "their state (plus the hero's position and the lighting weights). WITH arguments it "
    "CHANGES THE RUNNING GAME, visibly and until set back or the game restarts — switch one "
    "thing, read refii_metrics, switch it back. Turn vsync off first or fps measures the "
    "display, not the machine.",
    _RENDER_LEVERS,
)
def render(args):
    request = {"op": "render"}
    for name in _RENDER_LEVERS:
        if name in args:
            request[name] = args[name]
    return ask(request)


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
