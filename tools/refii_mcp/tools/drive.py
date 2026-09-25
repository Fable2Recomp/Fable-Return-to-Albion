"""Driving the run: the pad, and waiting for the state a press was meant to reach."""
from ..channel import ask
from ..registry import tool

# The guest's own button bits, as XInput numbers them.
BUTTONS = {
    "dpad_up": 0x0001, "dpad_down": 0x0002, "dpad_left": 0x0004, "dpad_right": 0x0008,
    "start": 0x0010, "back": 0x0020,
    "left_thumb": 0x0040, "right_thumb": 0x0080,
    "left_shoulder": 0x0100, "right_shoulder": 0x0200,
    "a": 0x1000, "b": 0x2000, "x": 0x4000, "y": 0x8000,
}


@tool(
    "refii_press",
    "Hold pad buttons for a few frames, as a player would. Goes to the game the handshake "
    "names NOW, so a press cannot land in a previous run. Names: "
    + ", ".join(sorted(BUTTONS))
    + ". Several may be given at once. A press that appears not to land on the title is "
      "usually the armed byte being false — check `refii_frontend`'s canPressA first.",
    {
        "buttons": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Button names to hold together.",
        },
        "frames": {
            "type": "integer",
            "description": "Frames to hold (default 8). The guest edge-detects.",
        },
        "pad": {"type": "integer", "description": "Guest pad 0..3 (default 0). START on a pad "
                "that is not player one's is the local co-op drop-in; the pad stays connected "
                "for the rest of the run once pressed."},
        "lx": {"type": "number", "description": "Left stick X, -1..1, held like the buttons."},
        "ly": {"type": "number", "description": "Left stick Y, -1..1 (up positive)."},
        "rx": {"type": "number", "description": "Right stick X, -1..1."},
        "ry": {"type": "number", "description": "Right stick Y, -1..1."},
        "lt": {"type": "number", "description": "Left trigger 0..1."},
        "rt": {"type": "number", "description": "Right trigger 0..1."},
    },
    ["buttons"],
)
def press(args):
    mask = 0
    unknown = []
    for button in args.get("buttons", []):
        bit = BUTTONS.get(str(button).lower())
        if bit is None:
            unknown.append(button)
        else:
            mask |= bit
    if unknown:
        return {"ok": False, "error": f"unknown button(s): {', '.join(unknown)}",
                "known": sorted(BUTTONS)}
    payload = {"op": "press", "buttons": mask, "frames": args.get("frames", 8)}
    if args.get("pad") is not None:
        payload["pad"] = int(args["pad"])
    for axis in ("lx", "ly", "rx", "ry", "lt", "rt"):
        if args.get(axis) is not None:
            payload[axis] = float(args[axis])
    if mask == 0 and not any(axis in payload for axis in ("lx", "ly", "rx", "ry", "lt", "rt")):
        return {"ok": False, "error": "no buttons named"}
    return ask(payload)


@tool(
    "refii_wait",
    "Block until the RUNNING game reaches a named state, answered by the game itself the "
    "frame it holds: 'frontend' (title or menu exists), 'frontend-armed' (the title takes A), "
    "'world' (the in-game screen manager exists), 'gameplay' (no screen up), or 'screen' with "
    "a screen id (0 = the pause root). Replaces polling loops that could not see what they "
    "waited for.",
    {
        "for": {
            "type": "string",
            "enum": ["frontend", "frontend-armed", "world", "gameplay", "screen", "log"],
        },
        "screen": {"type": "integer", "description": "With for='screen': the id."},
        "match": {"type": "string",
                  "description": "With for='log': waits for a NEW godot.log line containing "
                                 "this (history never satisfies it)."},
        "timeout": {"type": "integer", "description": "Seconds (default 120, max 570)."},
    },
    ["for"],
)
def wait(args):
    timeout = max(1, min(int(args.get("timeout", 120)), 570))
    return ask(
        {
            "op": "wait",
            "for": args.get("for", ""),
            "screen": args.get("screen", 0),
            "match": args.get("match", ""),
            "timeout": timeout,
        },
        timeout=timeout + 15,
    )


@tool(
    "refii_menu",
    "Drive a menu with the GUEST'S OWN verbs instead of synthesized pad input — the stick "
    "latch and edge detection stay its problem. verb: up/down/left/right (held=true for a "
    "latched scroll), select, action (a screen input code, default 36=A; atRoot for the "
    "root), event (name/type/id posted as a MenuEvent).",
    {
        "verb": {"type": "string",
                 "enum": ["up", "down", "left", "right", "select", "action", "event"]},
        "held": {"type": "boolean", "description": "Scrolls: latch held (default false)."},
        "code": {"type": "integer", "description": "action: the input code (36=A, 37=back)."},
        "atRoot": {"type": "boolean", "description": "action: aim at the pause root."},
        "name": {"type": "string", "description": "event: the row name."},
        "type": {"type": "integer", "description": "event: the event type."},
        "id": {"type": "integer", "description": "event: the row id."},
    },
    ["verb"],
)
def menu(args):
    payload = {"op": "menu"}
    payload.update({key: args[key] for key in
                    ("verb", "held", "code", "atRoot", "name", "type", "id")
                    if args.get(key) is not None})
    return ask(payload)


@tool(
    "refii_action",
    "Post a named action onto the guest's tick-thread queue — the same lever the host's own "
    "menus use (New Game, LoadSave:N, MenuSelect, GuiLua:..., ...). Returns the queue depth; "
    "a depth that climbs means the pump is not draining.",
    {
        "name": {"type": "string", "description": "The action string, verbatim."},
    },
    ["name"],
)
def action(args):
    return ask({"op": "action", "name": str(args.get("name", ""))})


@tool(
    "refii_replay",
    "Record the published pad stream, or replay a tape — a repro becomes a script. "
    "do='start' arms recording, 'stop' saves the tape (returns its path), 'play' feeds a "
    "tape back frame-for-frame (the game-state input masks still apply, because they are "
    "the game's).",
    {
        "do": {"type": "string", "enum": ["start", "stop", "play"]},
        "path": {"type": "string", "description": "stop: save here; play: the tape to feed."},
    },
    ["do"],
)
def replay(args):
    what = args.get("do")
    if what in ("start", "stop"):
        payload = {"op": "input-record", "do": what}
        if args.get("path"):
            payload["path"] = str(args["path"])
        return ask(payload)
    if what == "play":
        if not args.get("path"):
            return {"ok": False, "error": "play needs the tape's path"}
        return ask({"op": "input-replay", "path": str(args["path"])})
    return {"ok": False, "error": "do must be start, stop or play"}
