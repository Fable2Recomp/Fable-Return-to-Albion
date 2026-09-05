"""Symbols and chains as tools of their own."""
from .. import chainfile, symbols
from ..registry import tool


@tool(
    "refii_sym",
    "Resolve guest addresses to IDA's names (function+offset, or a named global) and names "
    "back to addresses — offline, against the exported symbol tables, no game needed. Give "
    "`addr` (hex, several comma-separated ok) or `name` (exact IDA name; renamed functions "
    "use their renamed form).",
    {
        "addr": {"type": "string", "description": "Address(es) to name, hex, comma-separated."},
        "name": {"type": "string", "description": "Name to locate."},
    },
)
def sym(args):
    if not symbols.loaded():
        return {"ok": False,
                "error": f"no symbol tables at {symbols.SYMBOLS} — re-run the IDA export"}
    if args.get("name"):
        addr = symbols.lookup(str(args["name"]))
        if addr is None:
            return {"ok": False, "error": f"nothing named {args['name']!r} in the export — "
                                          "renamed since, or never existed; check in IDA"}
        return {"ok": True, "name": args["name"], "addr": f"{addr:08X}"}
    if args.get("addr"):
        out = {}
        for part in str(args["addr"]).split(","):
            text = part.strip().removeprefix("0x")
            try:
                resolved = symbols.resolve(int(text, 16))
            except ValueError:
                resolved = "not hex"
            out[text.upper()] = resolved or "no symbol"
        return {"ok": True, "resolved": out}
    return {"ok": False, "error": "give addr or name"}


@tool(
    "refii_chains",
    "List the named pointer chains in chains.toml — each one a VERIFIED deref recipe with "
    "its note and provenance. Use a name via refii_peek's `chain` parameter. New chains "
    "enter the file only with their verification named; that rule is the whole point.",
)
def chains(_args):
    listing = {}
    for name, entry in sorted(chainfile.all_chains().items()):
        if isinstance(entry, dict):
            listing[name] = {
                "base": entry.get("base"),
                "deref": [hex(int(o)) for o in entry.get("deref", [])],
                "note": entry.get("note", ""),
                "verified": entry.get("verified", ""),
            }
    return {"ok": True, "chains": listing, "file": chainfile.CHAINS}
