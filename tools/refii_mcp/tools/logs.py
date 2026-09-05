"""The running game's own log, without leaving the channel."""
from ..channel import ask
from ..registry import tool


@tool(
    "refii_log",
    "The tail of the RUNNING game's own log, optionally filtered to lines containing `match` "
    "(case-insensitive). Asks a question of the live run instead of hunting for the right file "
    "afterwards — and it is the running game's log, not a months-old segment from a previous "
    "install.",
    {
        "lines": {"type": "integer", "description": "How many lines (default 40)."},
        "match": {"type": "string", "description": "Only lines containing this."},
    },
)
def log(args):
    return ask(
        {"op": "log", "lines": args.get("lines", 40), "match": args.get("match", "")},
        timeout=15.0,
    )


@tool(
    "refii_guestlog",
    "The tail of the EMBEDDED guest's own segmented logs (refii_*.log beside the game "
    "binary — the ones the usual location does not hold) or the launcher's log. "
    "Segment-aware, newest file first.",
    {
        "which": {"type": "string", "enum": ["guest", "launcher"],
                  "description": "Which log family (default guest)."},
        "lines": {"type": "integer", "description": "How many lines (default 40)."},
        "match": {"type": "string", "description": "Only lines containing this."},
    },
)
def guestlog(args):
    return ask(
        {"op": "guestlog", "which": args.get("which", "guest"),
         "lines": args.get("lines", 40), "match": args.get("match", "")},
        timeout=15.0,
    )
