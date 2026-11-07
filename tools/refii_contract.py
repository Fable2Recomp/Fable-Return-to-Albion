#!/usr/bin/env python3
"""UI contract runner: presses, waits and expectations from a TOML script, against the live
debug channel. Exit 0 = every expectation held; anything else names the step that failed.

Usage:  python3 tools/refii_contract.py tools/contracts/<name>.toml [more...]

Script format — [[step]] tables, executed in order:
  { wait = "frontend-armed", timeout = 300 }        # any refii_wait condition
  { press = ["a"], frames = 8 }                     # buttons; lx/ly/rx/ry/lt/rt ride along
  { menu = "down" }                                 # a guest menu verb
  { sleep = 1.5 }                                   # seconds
  { mark = "entering options" }                     # a log + Tracy marker
  { shot = "options" }                              # keep a labelled screenshot
  { expect = { op="state", path="screen", equals="Options" } }   # an assertion

A failed expect stops the script (later steps assume the state it checked).
"""
import os
import sys
import time
import tomllib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from refii_mcp import registry, tools  # noqa: E402,F401
from refii_mcp.channel import ask     # noqa: E402


def run(path):
    with open(path, "rb") as file:
        script = tomllib.load(file)
    steps = script.get("step", [])
    print(f"== {os.path.basename(path)}: {len(steps)} steps")
    for index, step in enumerate(steps, 1):
        label = f"  [{index:>2}/{len(steps)}]"
        if "sleep" in step:
            time.sleep(float(step["sleep"]))
            continue
        if "wait" in step:
            reply = registry.call("refii_wait", {
                "for": step["wait"], "screen": step.get("screen", 0),
                "match": step.get("match", ""), "timeout": step.get("timeout", 120)})
            if not reply.get("ok") or not reply.get("met"):
                print(f"{label} WAIT FAILED for {step['wait']!r}: {reply}")
                return 1
            print(f"{label} waited: {step['wait']}")
        elif "press" in step:
            payload = {"buttons": step["press"], "frames": step.get("frames", 8)}
            payload.update({k: step[k] for k in ("lx", "ly", "rx", "ry", "lt", "rt") if k in step})
            reply = registry.call("refii_press", payload)
            if not reply.get("ok"):
                print(f"{label} PRESS FAILED: {reply}")
                return 1
            print(f"{label} pressed: {'+'.join(step['press'])}")
            time.sleep(step.get("settle", 0.7))
        elif "menu" in step:
            reply = registry.call("refii_menu", {"verb": step["menu"], **{
                k: step[k] for k in ("held", "code", "name", "type", "id")
                if k in step}})
            if not reply.get("ok"):
                print(f"{label} MENU FAILED: {reply}")
                return 1
            print(f"{label} menu: {step['menu']}")
            time.sleep(step.get("settle", 0.7))
        elif "mark" in step:
            registry.call("refii_mark", {"text": str(step["mark"])})
        elif "shot" in step:
            reply = registry.call("refii_shot", {"name": str(step["shot"]), "inline": False})
            print(f"{label} shot: {reply.get('path', reply.get('error'))}")
        elif "expect" in step:
            reply = registry.call("refii_expect", {"checks": [step["expect"]]})
            check = (reply.get("checks") or [{}])[0]
            if not reply.get("ok"):
                print(f"{label} EXPECT FAILED at {check.get('path')!r}: "
                      f"{check.get('failed')} (value: {check.get('value')!r})")
                return 1
            print(f"{label} expect: {check.get('path')} = {check.get('value')!r}")
        else:
            print(f"{label} unknown step {step!r}")
            return 1
    print("== PASSED")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    for target in sys.argv[1:]:
        code = run(target)
        if code != 0:
            sys.exit(code)
