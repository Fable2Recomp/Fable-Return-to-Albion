#!/usr/bin/env python3
"""Manual control of the running game's debug channel, without the MCP server.

    refii_debug.py                              interactive shell (tab completion, history)
    refii_debug.py <op> [key=value ...] [.path ...]
    refii_debug.py watch <seconds> <op> [key=value ...] [.path ...]

Values are JSON when they parse (true, 3, 1.5, null, "text", [1,2]) and plain strings otherwise.
A `.path` argument picks fields out of the reply: `.fps`, `.phases.frame_total.avgMs`,
`.viewports[0].size`, `.passes[].pass` (`[]` maps over a list). Several paths print one per line.

Shell-only commands: `set key=value ...` (render levers), `levers`, `watch`, `raw {json}`,
`ops`, `help`, `quit`. The port comes from the handshake file on every request
(tools/refii_mcp/channel.py), so a restarted game is picked up without reconnecting.
Standard library only.
"""
import json
import os
import re
import shlex
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "refii_mcp"))
import channel  # noqa: E402

# The `case "…":` labels of Godot/src/game/GameDebugServer.cs; merged with the server's own list.
KNOWN_OPS = [
    "ping", "state", "rows", "frontend", "wait", "peek", "gdb", "threads", "objects", "perf",
    "exports", "cvars", "cvar-set", "vfs", "sockets", "menu", "action", "call", "lua",
    "call-result", "poke", "heap", "probe", "guestlog", "mark", "lights", "fx", "fxlayer",
    "render", "entity", "twins", "casters", "doubles", "lodcensus", "gpuprofile", "capture",
    "metrics", "input-record", "input-replay", "achievements", "elements", "log", "hud", "watch",
    "alpha", "camera", "shot", "flicker", "press", "coop",
]
SHELL_COMMANDS = ["set", "levers", "watch", "raw", "ops", "help", "quit", "exit"]
HISTORY = os.path.expanduser("~/.cache/refii-scratch/refii_debug_history")
PATH_TOKEN = re.compile(r"\.([^.\[\]]+)|\[(\d*)\]")


def parse_value(text):
    try:
        return json.loads(text)
    except ValueError:
        return text


def build_request(op, args):
    """Splits `key=value` fields from `.path` filters. Returns (request, paths)."""
    request, paths = {"op": op}, []
    for arg in args:
        if arg.startswith("."):
            paths.append(arg)
        elif "=" in arg:
            key, value = arg.split("=", 1)
            request[key] = parse_value(value)
        else:
            raise ValueError(f"'{arg}' is neither key=value nor a .path")
    return request, paths


def pick(value, path):
    """Applies one `.a.b[0].c` / `[]` path; a missing step yields None."""
    values = [value]
    for key, index in PATH_TOKEN.findall(path):
        step = []
        for v in values:
            if key:
                step.append(v.get(key) if isinstance(v, dict) else None)
            elif index == "":
                step.extend(v if isinstance(v, list) else [None])
            else:
                i = int(index)
                step.append(v[i] if isinstance(v, list) and i < len(v) else None)
        values = step
    return values if "[]" in path else values[0]


def show(reply, paths, compact=False):
    """Prints a reply; False when it is an error."""
    if not reply.get("ok", True) and "error" in reply:
        print(f"error: {reply['error']}", file=sys.stderr)
        if "ops" in reply:
            print("ops: " + " ".join(reply["ops"]), file=sys.stderr)
        return False
    if reply.get("restarted"):
        print(f"note: {reply.get('note')}", file=sys.stderr)
    if not paths:
        for noise in ("ok", "pid", "boot", "uptime"):
            reply.pop(noise, None)
        print(json.dumps(reply, indent=None if compact else 1, ensure_ascii=False))
        return True
    for path in paths:
        value = pick(reply, path)
        text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
        print(f"{path} = {text}" if len(paths) > 1 else text)
    return True


def send(op, args, compact=False):
    request, paths = build_request(op, args)
    # `wait` holds its reply until the condition holds; `capture` replays a frame.
    timeout = 5.0 + float(request.get("timeout", 0) or 0) + (120 if op == "capture" else 0)
    return show(channel.ask(request, timeout=timeout), paths, compact)


def watch(seconds, op, args):
    request, paths = build_request(op, args)
    try:
        while True:
            reply = channel.ask(request)
            stamp = time.strftime("%H:%M:%S")
            if not paths:
                print(stamp, json.dumps(reply, ensure_ascii=False)[:400], flush=True)
            else:
                picked = [pick(reply, p) for p in paths]
                print(stamp, "  ".join(f"{p}={json.dumps(v, ensure_ascii=False)}"
                                       for p, v in zip(paths, picked)), flush=True)
            time.sleep(seconds)
    except KeyboardInterrupt:
        print()


def server_ops():
    reply = channel.ask({"op": "?"})
    return sorted(set(KNOWN_OPS) | set(reply.get("ops", [])))


def run_line(line):
    """One shell line. Returns False to leave the shell."""
    try:
        words = shlex.split(line)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return True
    if not words:
        return True
    head, rest = words[0], words[1:]
    try:
        if head in ("quit", "exit"):
            return False
        if head == "help":
            print(__doc__)
        elif head == "ops":
            print(" ".join(server_ops()))
        elif head == "levers":
            send("render", rest)
        elif head == "set":
            send("render", rest)
        elif head == "raw":
            show(channel.ask(json.loads(line.split(None, 1)[1])), [])
        elif head == "watch":
            if len(rest) < 2:
                raise ValueError("watch <seconds> <op> [key=value ...] [.path ...]")
            watch(float(rest[0]), rest[1], rest[2:])
        else:
            send(head, rest)
    except (ValueError, IndexError) as error:
        print(f"error: {error}", file=sys.stderr)
    return True


def shell():
    try:
        import readline
    except ImportError:  # Windows without pyreadline: no completion, same commands
        readline = None

    render_keys = []
    if readline is not None:
        render_reply = channel.ask({"op": "render"})
        render_keys = sorted(k for k in render_reply if k not in ("ok", "pid", "boot", "uptime"))
        words = sorted(set(server_ops() + SHELL_COMMANDS))

        def complete(text, state):
            buffer = readline.get_line_buffer().lstrip()
            first = buffer.split(" ", 1)[0]
            pool = words if " " not in buffer else \
                [k + "=" for k in render_keys] if first in ("set", "levers") else []
            matches = [w for w in pool if w.startswith(text)]
            return matches[state] if state < len(matches) else None

        readline.set_completer(complete)
        readline.set_completer_delims(" ")
        readline.parse_and_bind("tab: complete")
        os.makedirs(os.path.dirname(HISTORY), exist_ok=True)
        try:
            readline.read_history_file(HISTORY)
        except OSError:
            pass

    info, error = channel.endpoint()
    print(f"refii debug channel — {error}" if info is None else
          f"refii debug channel — pid {info.get('pid')} port {info.get('port')}  (help, tab completes)")
    try:
        while True:
            try:
                line = input("refii> ")
            except EOFError:
                print()
                break
            except KeyboardInterrupt:
                print()
                continue
            if not run_line(line):
                break
    finally:
        if readline is not None:
            readline.write_history_file(HISTORY)


def main(argv):
    if not argv:
        shell()
        return 0
    if argv[0] in ("-h", "--help", "help"):
        print(__doc__)
        return 0
    compact = "--compact" in argv
    argv = [a for a in argv if a != "--compact"]
    try:
        if argv[0] == "watch":
            watch(float(argv[1]), argv[2], argv[3:])
        elif not send(argv[0], argv[1:], compact):
            return 1
    except (ValueError, IndexError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
