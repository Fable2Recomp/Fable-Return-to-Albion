"""IDA live overlay: turn arena reads into an idapython script that annotates the database."""
import os
import time

from .. import arena, symbols
from ..registry import tool


@tool(
    "refii_overlay",
    "Write an idapython script that stamps LIVE guest values as comments at an address — "
    "run it in IDA (py_exec_file on the IDA MCP) and the decompile shows what the struct "
    "held at this moment. Reads through the arena, so it works on frozen and post-mortem "
    "runs. Comments are prefixed [live] with a timestamp so stale ones are recognizable.",
    {
        "addr": {"type": "string", "description": "Guest address, hex — or an IDA name."},
        "words": {"type": "integer", "description": "Words to annotate (default 16, max 64)."},
    },
    ["addr"],
)
def overlay(args):
    text = str(args.get("addr", ""))
    try:
        addr = int(text.removeprefix("0x"), 16)
    except ValueError:
        addr = symbols.lookup(text) or 0
        if addr == 0:
            return {"ok": False, "error": f"{text!r} is neither hex nor a known symbol"}

    count = max(1, min(int(args.get("words", 16)), 64))
    reader, description, error = arena.find_reader()
    if reader is None:
        return {"ok": False, "error": error}
    try:
        data = reader.read(addr, count * 4)
    finally:
        reader.close()
    if not data:
        return {"ok": False, "error": f"{addr:08X} is outside every guest view"}

    stamp = time.strftime("%H:%M:%S")
    lines = ["import idc", ""]
    for i in range(len(data) // 4):
        word = int.from_bytes(data[i * 4:(i + 1) * 4], "big")
        symbol = symbols.resolve(word)
        comment = f"[live {stamp}] {word:08X}" + (f" ({symbol})" if symbol else "")
        lines.append(f"idc.set_cmt(0x{addr + i * 4:08X}, {comment!r}, 0)")
    lines.append(f"print('annotated {len(data) // 4} words at {addr:08X}')")

    out_dir = os.path.expanduser("~/.cache/refii-scratch/mcp-overlays")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, f"overlay-{addr:08X}.py")
    with open(path, "w") as script:
        script.write("\n".join(lines) + "\n")
    return {"ok": True, "script": path, "addr": f"{addr:08X}", "words": len(data) // 4,
            "arena": description,
            "next": "run it with mcp__ida-pro-mcp__py_exec_file"}
