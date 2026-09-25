"""GPU time per pass in a RenderDoc capture of the game, headlessly.

Runs INSIDE qrenderdoc's embedded Python — there is no standalone module in the RenderDoc
tarball — so it is started as

    RDC_PATH=<capture.rdc> RDC_OUT=<report.json> qrenderdoc --python tools/renderdoc/rdc_passes.py

(the `refii_capture` MCP tool does exactly this with analyze=true). The script loads the
capture, replays it, fetches the GPU duration counter for every event, and sums it three ways:

- by RENDER TARGET — the colour and depth attachments bound at each draw, named where the
  engine named them and otherwise described by size and format. Works whether or not the
  capture carries debug markers.
- by MARKER — the debug-label regions when the capture has them (Godot emits them only with
  VK_EXT_debug_utils on, which the RenderDoc layer itself provides).
- by DRAW, the top forty.

It writes the report as JSON to RDC_OUT and exits the process: this is a batch tool, not a UI.
"""
import json
import os
import sys

import renderdoc as rd  # noqa: E402  (provided by qrenderdoc)


_textures = {}
_described = {}


def _describe_texture(controller, resource_id, names):
    if resource_id is None or resource_id == rd.ResourceId.Null():
        return None
    if resource_id in _described:
        return _described[resource_id]
    if not _textures:
        for tex in controller.GetTextures():
            _textures[tex.resourceId] = tex
    name = names.get(resource_id)
    tex = _textures.get(resource_id)
    if tex is None:
        text = name or str(resource_id)
    else:
        fmt = tex.format.Name() if hasattr(tex.format, "Name") else str(tex.format.type)
        text = f"{name or 'tex'} {tex.width}x{tex.height}{'x' + str(tex.depth) if tex.depth > 1 else ''} {fmt}"
    _described[resource_id] = text
    return text


def _walk(actions, out):
    for action in actions:
        out.append(action)
        if action.children:
            _walk(action.children, out)


def _analyse(controller):
    counter = rd.GPUCounter.EventGPUDuration
    available = [c for c in controller.EnumerateCounters() if c == counter]
    if not available:
        return {"error": "this driver exposes no per-event GPU duration counter"}

    unit_seconds = True
    desc = controller.DescribeCounter(counter)
    if desc.unit != rd.CounterUnit.Seconds:
        unit_seconds = False

    duration = {}
    for result in controller.FetchCounters([counter]):
        value = result.value.d if desc.resultType == rd.CompType.Float else float(result.value.u64)
        duration[result.eventId] = value if unit_seconds else value / 1e9

    names = {}
    for res in controller.GetResources():
        names[res.resourceId] = res.name

    actions = []
    _walk(controller.GetRootActions(), actions)

    draw_flags = rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch | rd.ActionFlags.MeshDispatch
    clear_flags = rd.ActionFlags.Clear
    copy_flags = rd.ActionFlags.Copy | rd.ActionFlags.Resolve

    by_target = {}
    by_marker = {}
    draws = []
    total = 0.0
    marker_stack = []

    for action in actions:
        if action.flags & rd.ActionFlags.PushMarker:
            marker_stack.append(action.customName or f"marker {action.eventId}")
            continue
        if action.flags & rd.ActionFlags.PopMarker:
            if marker_stack:
                marker_stack.pop()
            continue

        seconds = duration.get(action.eventId)
        if seconds is None:
            continue
        ms = seconds * 1000.0
        total += ms

        kind = "draw" if action.flags & draw_flags else "clear" if action.flags & clear_flags \
            else "copy" if action.flags & copy_flags else "other"

        key = "(no target)"
        if kind in ("draw", "clear"):
            # The action's own attachments (`outputs`, `depthOut`) are recorded at capture time,
            # so no `SetFrameEvent` per draw: that re-executes the frame prefix every time.
            targets = [t for t in action.outputs if t != rd.ResourceId.Null()]
            described = [d for d in (_describe_texture(controller, t, names) for t in targets) if d]
            depth_described = _describe_texture(controller, action.depthOut, names)
            if depth_described:
                described.append("depth " + depth_described)
            if described:
                key = " + ".join(described)

        entry = by_target.setdefault(key, {"ms": 0.0, "events": 0, "draws": 0})
        entry["ms"] += ms
        entry["events"] += 1
        if kind == "draw":
            entry["draws"] += 1

        marker = " / ".join(marker_stack) if marker_stack else "(unmarked)"
        m = by_marker.setdefault(marker, {"ms": 0.0, "events": 0})
        m["ms"] += ms
        m["events"] += 1

        draws.append({"eid": action.eventId, "ms": round(ms, 4), "kind": kind,
                      "name": action.GetName(controller.GetStructuredFile()), "target": key})

    def ordered(table):
        rows = [{"key": k, **{kk: (round(vv, 3) if isinstance(vv, float) else vv) for kk, vv in v.items()}}
                for k, v in table.items()]
        rows.sort(key=lambda r: r["ms"], reverse=True)
        return rows

    draws.sort(key=lambda d: d["ms"], reverse=True)
    return {
        "totalGpuMs": round(total, 3),
        "events": len(duration),
        "byTarget": ordered(by_target),
        "byMarker": ordered(by_marker),
        "topDraws": draws[:40],
    }


def main():
    path = os.environ.get("RDC_PATH", "")
    out = os.environ.get("RDC_OUT", path + ".passes.json")
    report = {"capture": path}
    try:
        if not path or not os.path.exists(path):
            raise RuntimeError("RDC_PATH does not name a capture")

        def failed(status):
            code = status.code if hasattr(status, "code") else status
            return code != rd.ResultCode.Succeeded

        cap = rd.OpenCaptureFile()
        status = cap.OpenFile(path, "", None)
        if failed(status):
            raise RuntimeError(f"OpenFile: {status}")
        if not cap.LocalReplaySupport():
            raise RuntimeError("no local replay support for this capture")
        status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
        if failed(status):
            raise RuntimeError(f"OpenCapture: {status}")
        try:
            report.update(_analyse(controller))
        finally:
            controller.Shutdown()
        cap.Shutdown()
    except Exception as error:  # noqa: BLE001 — the report carries the reason
        report["error"] = str(error)

    with open(out, "w") as handle:
        json.dump(report, handle, indent=1)
    sys.stdout.write(json.dumps({"written": out, "totalGpuMs": report.get("totalGpuMs"),
                                 "error": report.get("error")}) + "\n")
    sys.stdout.flush()
    if os.environ.get("RDC_NO_EXIT") != "1":
        os._exit(0)


main()
