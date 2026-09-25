"""Proves the RenderDoc chain without the game: launches vkcube under the layer, queues a
capture of one frame through target control, waits for the file, and runs rdc_passes.py on it.

    RDC_SELFTEST_OUT=<dir> qrenderdoc --python tools/renderdoc/rdc_selftest.py

Exits the process when done; the report and the capture are left in RDC_SELFTEST_OUT.
"""
import json
import os
import sys
import time

import renderdoc as rd  # noqa: E402  (provided by qrenderdoc)

out_dir = os.environ.get("RDC_SELFTEST_OUT", os.path.expanduser("~/.cache/refii-scratch/renderdoc"))
os.makedirs(out_dir, exist_ok=True)
template = os.path.join(out_dir, "selftest")
result = {"ok": False}

try:
    opts = rd.CaptureOptions()
    opts.captureCallstacks = False
    opts.apiValidation = False
    launched = rd.ExecuteAndInject("vkcube", out_dir, "--c 600", [], template, opts, False)
    code = launched.result.code if hasattr(launched.result, "code") else launched.result
    if code != rd.ResultCode.Succeeded:
        raise RuntimeError(f"ExecuteAndInject: {launched.result}")

    control = rd.CreateTargetControl("localhost", launched.ident, "refii-selftest", True)
    if control is None:
        raise RuntimeError("no target control connection to the injected process")

    control.QueueCapture(30, 1)
    capture_path = None
    deadline = time.time() + 30
    while time.time() < deadline and capture_path is None:
        msg = control.ReceiveMessage(None)
        if msg.type == rd.TargetControlMessageType.NewCapture:
            capture_path = msg.newCapture.path
        elif msg.type == rd.TargetControlMessageType.Disconnected:
            break
        time.sleep(0.05)
    control.Shutdown()
    if capture_path is None:
        raise RuntimeError("no capture arrived within 30 s")

    result["capture"] = capture_path
    os.environ["RDC_PATH"] = capture_path
    os.environ["RDC_OUT"] = capture_path + ".passes.json"
    os.environ["RDC_NO_EXIT"] = "1"
    # No __file__ under qrenderdoc's interpreter: the sibling script is found from the tree
    # this is run in (RDC_TOOLS_DIR, or tools/renderdoc under the working directory).
    here = os.environ.get("RDC_TOOLS_DIR", os.path.join(os.getcwd(), "tools", "renderdoc"))
    with open(os.path.join(here, "rdc_passes.py")) as handle:
        code = handle.read()
    exec(compile(code, "rdc_passes.py", "exec"), {"__name__": "rdc_passes"})
    with open(os.environ["RDC_OUT"]) as handle:
        result["passes"] = json.load(handle)
    result["ok"] = "error" not in result["passes"]
except Exception as error:  # noqa: BLE001
    result["error"] = str(error)

with open(os.path.join(out_dir, "selftest.json"), "w") as handle:
    json.dump(result, handle, indent=1)
sys.stdout.write(json.dumps({k: v for k, v in result.items() if k != "passes"}) + "\n")
sys.stdout.flush()
os._exit(0 if result["ok"] else 1)
