"""Execution instruments: dynamic probes, guest calls, Lua, poke, heap lookup.

Everything here needs the ABI 113 build. The probes are recording trampolines, not
breakpoints — nothing stops, records are best-effort racy, and that is the deal.
"""
from .. import symbols
from ..channel import ask
from ..registry import tool


@tool(
    "refii_probe",
    "Swap a guest function's dispatch slot for a recording trampoline: counts calls, keeps "
    "the last r3..r8 and LR, runs the original. Who calls sub_82XXXXXX, how often, with "
    "what — no rebuild, nothing stopped. do='set' (addr, or a symbol name), 'clear' (slot), "
    "'read' (all armed slots). 16 slots. Only works on recompiled function STARTS.",
    {
        "do": {"type": "string", "enum": ["set", "clear", "read"]},
        "addr": {"type": "string", "description": "set: function start, hex — or an IDA name."},
        "slot": {"type": "integer", "description": "clear: the slot to disarm."},
    },
    ["do"],
)
def probe(args):
    what = args.get("do")
    if what == "set":
        text = str(args.get("addr", ""))
        try:
            addr = int(text.removeprefix("0x"), 16)
        except ValueError:
            addr = symbols.lookup(text) or 0
            if addr == 0:
                return {"ok": False, "error": f"{text!r} is neither hex nor a known symbol"}
        reply = ask({"op": "probe", "do": "set", "addr": f"{addr:08X}"})
        if reply.get("ok") and symbols.loaded():
            reply["symbol"] = symbols.resolve(addr)
        return reply
    if what == "clear":
        return ask({"op": "probe", "do": "clear", "slot": args.get("slot", -1)})
    reply = ask({"op": "probe", "do": "read"})
    if reply.get("ok") and symbols.loaded():
        for entry in reply.get("probes", []):
            entry["symbol"] = symbols.resolve(int(entry["addr"], 16))
            entry["lastLr"] = symbols.annotate([entry["lastLr"]])[0]
    return reply


@tool(
    "refii_call",
    "Call one guest function on the GUEST'S OWN tick thread with up to four register "
    "arguments, and wait for its r3. The REPL primitive: test a decompiled contract without "
    "a rebuild. The call runs when the tick pump next drains — a run parked outside the "
    "front end holds it (the reply says so). Use with the same care as gdb's `call`: a "
    "function with side effects HAS those side effects.",
    {
        "addr": {"type": "string", "description": "Function start, hex — or an IDA name."},
        "args": {"type": "array", "items": {"type": "string"},
                 "description": "Up to 4 hex values for r3..r6."},
        "timeout": {"type": "integer", "description": "Seconds to wait for the result (default 10)."},
    },
    ["addr"],
)
def call(args):
    import time
    text = str(args.get("addr", ""))
    try:
        addr = int(text.removeprefix("0x"), 16)
    except ValueError:
        addr = symbols.lookup(text) or 0
        if addr == 0:
            return {"ok": False, "error": f"{text!r} is neither hex nor a known symbol"}
    posted = ask({"op": "call", "addr": f"{addr:08X}", "args": args.get("args", [])})
    if not posted.get("ok"):
        return posted
    before = posted.get("seqBefore", 0)
    deadline = time.monotonic() + min(int(args.get("timeout", 10)), 60)
    while time.monotonic() < deadline:
        time.sleep(0.2)
        result = ask({"op": "call-result"})
        if result.get("ok") and result.get("seq", 0) > before:
            if symbols.loaded():
                result["symbol"] = symbols.resolve(addr)
            return result
    return {"ok": False, "error": "posted but no result yet — the tick pump is not draining "
                                  "(front end down, or the guest is parked); check later with "
                                  "the same call or refii_threads", "queued": posted.get("queued")}


@tool(
    "refii_lua",
    "Run a Lua chunk in the guest's live GUI state, on its own tick thread, and report "
    "whether it ran. The g_Menu / GUI globals are the shipped scripts' own world — the same "
    "state the ported C# mirrors. Needs a live presentation; the reply says when there was "
    "none.",
    {
        "chunk": {"type": "string", "description": "The Lua source to run."},
        "timeout": {"type": "integer", "description": "Seconds to wait (default 10)."},
    },
    ["chunk"],
)
def lua(args):
    import time
    posted = ask({"op": "lua", "chunk": str(args.get("chunk", ""))})
    if not posted.get("ok"):
        return posted
    before = posted.get("seqBefore", 0)
    deadline = time.monotonic() + min(int(args.get("timeout", 10)), 60)
    while time.monotonic() < deadline:
        time.sleep(0.2)
        result = ask({"op": "call-result"})
        if result.get("ok") and result.get("seq", 0) > before:
            return result
    return {"ok": False, "error": "posted but no result yet — the tick pump is not draining",
            "queued": posted.get("queued")}


@tool(
    "refii_poke",
    "Write ONE guest word — the flip-a-byte-and-look experiment. Refused unless the page is "
    "heap-tracked and writable, so protected text cannot be hit by accident. Deliberate "
    "state mutation: say why in the surrounding work, and prefer refii_diff first to know "
    "what you are flipping.",
    {
        "addr": {"type": "string", "description": "Guest address, hex."},
        "value": {"type": "string", "description": "The 32-bit value, hex."},
    },
    ["addr", "value"],
)
def poke(args):
    return ask({"op": "poke", "addr": str(args.get("addr", "0")),
                "value": str(args.get("value", "0"))})


@tool(
    "refii_heap",
    "Which guest heap owns an address, its page size and protection — the validity check "
    "before trusting a pointer chain's hop, and the writability gate refii_poke enforces.",
    {
        "addr": {"type": "string", "description": "Guest address, hex."},
    },
    ["addr"],
)
def heap(args):
    return ask({"op": "heap", "addr": str(args.get("addr", "0"))})
