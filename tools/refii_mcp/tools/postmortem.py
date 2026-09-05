"""After the run died: the flight recorder, the leaked arena, the core dump."""
import glob
import os
import shutil
import subprocess
import sys

from .. import arena, symbols
from ..channel import ask
from ..registry import tool


def _userdata_logs():
    """Every Godot app_userdata logs dir, newest project first."""
    if sys.platform == "win32":
        root = os.path.join(os.environ.get("APPDATA", ""), "Godot", "app_userdata")
    else:
        root = os.path.expanduser("~/.local/share/godot/app_userdata")
    dirs = glob.glob(os.path.join(root, "*", "logs"))
    dirs.sort(key=lambda d: os.path.getmtime(d), reverse=True)
    return dirs


@tool(
    "refii_flight",
    "The flight recorder's tail: once a second the game appends what it was doing — screen, "
    "guest state, every guest thread's r1/lr. After a freeze or a silent death the LAST LINE "
    "says where it stood, and its r1 values let refii_dump walk the stacks from the arena "
    "with no channel at all. Reads the newest refii_flight.jsonl across the app_userdata "
    "projects.",
    {
        "lines": {"type": "integer", "description": "How many trailing entries (default 10)."},
    },
)
def flight(args):
    take = max(1, min(int(args.get("lines", 10)), 200))
    for logs in _userdata_logs():
        path = os.path.join(logs, "refii_flight.jsonl")
        if not os.path.exists(path):
            continue
        with open(path) as file:
            lines = file.readlines()[-take:]
        entries = [line.rstrip("\n") for line in lines]
        if symbols.loaded():
            import json
            decoded = []
            for line in entries:
                try:
                    entry = json.loads(line)
                except ValueError:
                    decoded.append(line)
                    continue
                threads = entry.get("threads")
                if isinstance(threads, list):
                    entry["threads"] = [
                        t + (f" {symbols.resolve(int(t.rsplit(':', 1)[-1], 16)) or ''}").rstrip()
                        for t in threads
                    ]
                decoded.append(entry)
            entries = decoded
        return {"ok": True, "path": path, "entries": entries}
    return {"ok": False, "error": "no refii_flight.jsonl found — the recorder ships with "
                                  "ABI 113; has that build run yet?"}


@tool(
    "refii_preserve",
    "Copy the newest leaked guest arena OUT of /dev/shm before the next launch's reclaim "
    "deletes it — relaunching destroys the only witness. Sparse-aware copy into "
    "~/.cache/refii-scratch/arenas/. Linux only; Windows keeps no post-mortem arena. Later "
    "reads: point REFII_DEBUG_HANDSHAKE at nothing and give the copy's path to the arena "
    "tools via a symlink into /dev/shm — or just read it before relaunching instead.",
    {
        "name": {"type": "string", "description": "Optional label for the copy."},
    },
)
def preserve(args):
    if sys.platform == "win32":
        return {"ok": False, "error": "Windows keeps no post-mortem arena — the mapping died "
                                      "with the process"}
    leaked = sorted(glob.glob("/dev/shm/xenia_memory_*"), key=os.path.getmtime)
    # NEVER the live run's arena: it is not a leak, and labelling it a crash preserve is how
    # a healthy run's memory gets read as a corpse later.
    from ..channel import handshake_info
    info, _ = handshake_info()
    if info is not None and info.get("alive") and info.get("arena"):
        leaked = [p for p in leaked if os.path.basename(p) != str(info["arena"])]
    if not leaked:
        return {"ok": False, "error": "no LEAKED xenia_memory_* to preserve — the only arena "
                                      "present belongs to the running game"}
    source = leaked[-1]
    keep_dir = os.path.expanduser("~/.cache/refii-scratch/arenas")
    os.makedirs(keep_dir, exist_ok=True)
    label = "".join(c if c.isalnum() else "-" for c in str(args.get("name", "crash")))
    target = os.path.join(keep_dir, f"{label}-{os.path.basename(source)}")
    copied = subprocess.run(["cp", "--sparse=always", source, target],
                            capture_output=True, text=True)
    if copied.returncode != 0:
        return {"ok": False, "error": copied.stderr[-300:]}
    return {"ok": True, "from": source, "to": target,
            "residentBytes": os.stat(target).st_blocks * 512,
            "note": "the original stays in /dev/shm until the next launch reclaims it"}


@tool(
    "refii_core",
    "The newest core dump for the game, via coredumpctl: when, why, and the crashing host "
    "C++ stack. The clean-exit SIGABRT writes one too, so 'there is a core' does not itself "
    "mean a crash — read the signal and the time.",
    {
        "match": {"type": "string",
                  "description": "Executable name filter (default: the launcher)."},
    },
)
def core(args):
    if shutil.which("coredumpctl") is None:
        return {"ok": False, "error": "no coredumpctl on this system"}
    match = str(args.get("match", "") or "refii")
    info = subprocess.run(["coredumpctl", "info", "--no-pager", "-1", match],
                          capture_output=True, text=True, timeout=30)
    if info.returncode != 0:
        return {"ok": False, "error": info.stderr.strip() or f"no core matching {match!r}"}
    text = info.stdout
    return {"ok": True, "info": text[:6000],
            "note": "for the full stack: coredumpctl debug -1 " + match}
