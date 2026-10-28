"""The ReXGlue runtime the guest runs in: threads, kernel objects, counters, exports, cvars,
the VFS and sockets. Read-only except `refii_cvar_set`, whose write the registry validates."""
import time

from .. import symbols
from ..channel import ask
from ..registry import tool


@tool(
    "refii_threads",
    "Every thread the guest's kernel knows, each guest thread with a GUEST BACKTRACE — "
    "return addresses innermost first, as hex for IDA. Frame 0 is the live LR (a leaf's "
    "return address exists nowhere else); the rest walk the title's own back chain "
    "(32-bit word at [r1], saved LR at back-chain minus 8, verified in the binary). "
    "THE freeze instrument: a hung run names its own stuck thread in one call instead of "
    "a gdb-as-parent session. Values are exact for parked threads, torn at worst for "
    "running ones — and the parked ones are the question.",
    {
        "bt": {"type": "boolean", "description": "Include backtraces (default true)."},
    },
)
def threads(args):
    reply = ask({"op": "threads", "bt": args.get("bt", True)}, timeout=15.0)
    if reply.get("ok") and symbols.loaded():
        for thread in reply.get("threads", []):
            if "lr" in thread:
                thread["lr"] = symbols.annotate([thread["lr"]])[0]
            if "backtrace" in thread:
                thread["backtrace"] = symbols.annotate(thread["backtrace"])
    return reply


@tool(
    "refii_objects",
    "The kernel handle table: every event, mutant, semaphore, thread, timer, file and "
    "socket, with type and name. Pairs with refii_threads — thread X waits, and this says "
    "on what. Also the live check that a cached handle still names the object it did "
    "(handles RECYCLE).",
    {
        "match": {"type": "string", "description": "Only names containing this."},
        "type": {
            "type": "string",
            "description": "Only this type: Event, Mutant, Semaphore, Thread, Timer, File, "
                           "Socket, Module, ...",
        },
    },
)
def objects(args):
    return ask(
        {"op": "objects", "match": args.get("match", ""), "type": args.get("type", "")},
        timeout=15.0,
    )


@tool(
    "refii_perf",
    "The runtime's rex::perf counters — functions dispatched, active threads, APC depth, "
    "critical-region contentions, XMA frames decoded, and more. Values are LIVE run totals "
    "or gauges, never per-frame rates. Pass `interval` to sample twice and get per-second "
    "deltas as well — that is how a counter becomes a rate.",
    {
        "interval": {
            "type": "number",
            "description": "Seconds between two samples (0 = single read, max 30).",
        },
    },
)
def perf(args):
    first = ask({"op": "perf"})
    interval = min(float(args.get("interval", 0) or 0), 30.0)
    if not first.get("ok") or interval <= 0:
        return first

    time.sleep(interval)
    second = ask({"op": "perf"})
    if not second.get("ok"):
        return second

    before = first.get("counters", {})
    after = second.get("counters", {})
    second["perSecond"] = {
        name: round((after[name] - before.get(name, 0)) / interval, 2)
        for name in after
    }
    second["interval"] = interval
    return second


@tool(
    "refii_exports",
    "The kernel export tables the title links against, as 'module!Name' with ordinal and "
    "whether the export is implemented. `unimplemented: true` lists exactly the stubs the "
    "title could be hitting. NO call counts — the runtime's call_count field is dead in "
    "recompiled builds; do not infer frequency from this list.",
    {
        "match": {"type": "string", "description": "Only names containing this."},
        "unimplemented": {
            "type": "boolean",
            "description": "Only exports with no implementation (default false).",
        },
        "limit": {"type": "integer", "description": "Max rows (default 200)."},
    },
)
def exports(args):
    return ask(
        {
            "op": "exports",
            "match": args.get("match", ""),
            "unimplemented": args.get("unimplemented", False),
            "limit": args.get("limit", 200),
        },
        timeout=15.0,
    )


@tool(
    "refii_cvars",
    "The runtime's cvar registry: every flag with live value, default, category, type and "
    "lifecycle, plus whether it has been modified. `match` filters on name OR category.",
    {
        "match": {"type": "string", "description": "Only names/categories containing this."},
    },
)
def cvars(args):
    return ask({"op": "cvars", "match": args.get("match", "")}, timeout=15.0)


@tool(
    "refii_cvar_set",
    "Set one runtime cvar, live. The registry's own validator decides — lifecycle "
    "(init-only flags refuse after boot), range and allowed values — and `accepted` "
    "reports its answer. A requires-restart flag can accept and still not take effect "
    "until the next run; check its lifecycle in refii_cvars first.",
    {
        "name": {"type": "string", "description": "The cvar's exact name."},
        "value": {"type": "string", "description": "New value, as text."},
    },
    ["name", "value"],
)
def cvar_set(args):
    return ask({
        "op": "cvar-set",
        "name": str(args.get("name", "")),
        "value": str(args.get("value", "")),
    })


@tool(
    "refii_vfs",
    "Resolve one guest path against the LIVE virtual file system: whether its root is a "
    "symbolic link (and to what), and whether the path resolves to an entry (absolute "
    "path, size, attributes). Distinguishes 'the link exists but resolves to nothing' "
    "from 'no such mount' — the wrong-naming failure this project has already had. "
    "Paths are guest-style, e.g. 'game:\\\\data\\\\globals.bnk'.",
    {
        "path": {"type": "string", "description": "Guest path to resolve."},
    },
    ["path"],
)
def vfs(args):
    return ask({"op": "vfs", "path": str(args.get("path", ""))}, timeout=15.0)


@tool(
    "refii_sockets",
    "Every socket in the kernel handle table, with its bound guest port and host native "
    "handle — the netplay-hang triage view.",
)
def sockets(_args):
    return ask({"op": "sockets"})


@tool(
    "refii_rumble",
    "Every guest pad's rumble: the joypad holding it, the motor pair the guest states this "
    "frame through XamInputSetState (0..65535, left = low-frequency), and what the joypad was "
    "last told (running, weak, strong), with both switches: guestSwitch (byte_8331924F, the "
    "Controls fold's Controller Vibration page) and launcherSwitch (controller_vibration), and "
    "handheld (running, amplitude): pad 0 on the phone's vibrator when played by touch on "
    "Android. A hit that leaves left/right at 0 never reached the guest's rumble; a non-zero "
    "pair with running=false is the host side.",
)
def rumble(_args):
    return ask({"op": "rumble"})
