"""The live channel: finding THIS game, asking it one question, and saying who answered.

Everything staleness-related lives here, because staleness is the failure this whole design
exists to prevent — a dead run answers exactly like a live one, and a coherent wrong conclusion
follows.
"""
import ctypes
import json
import os
import socket
import sys

HOST = "127.0.0.1"

# The game publishes {pid, port, boot} here on start and removes it on exit. It is read on
# EVERY request, never cached.
#
# WHY NOT A FIXED PORT. The game restarts constantly and this server does not — it lives as
# long as the Claude Code session. On a fixed port, a previous instance that outlived its run
# keeps the socket, the new game cannot bind, and every question is answered by the corpse:
# screenshots of a dead run, button presses into a dead run, and a perfectly coherent wrong
# conclusion. That is not hypothetical; a `pkill` whose pattern matched its own command line
# left exactly such a zombie running for twenty minutes in this project.
HANDSHAKE = os.environ.get(
    "REFII_DEBUG_HANDSHAKE", os.path.expanduser("~/.cache/refii-scratch/refii-debug.json")
)

# Where `shot` frames go. NEVER the repo or the package — they are frames of the player's own
# game data. The scratch directory is on /home; / is small and has hit ENOSPC in this project.
SHOTS = os.environ.get(
    "REFII_MCP_SHOTS", os.path.expanduser("~/.cache/refii-scratch/mcp-shots")
)

# The boot id last seen, so "the game restarted under you" can be SAID rather than missed.
_last_boot = None


def alive(pid):
    """Whether that pid is still running. A handshake outlives a game killed with SIGKILL.

    NEVER os.kill ON WINDOWS: for any signal but the two CTRL events, os.kill there is
    TerminateProcess — the liveness probe would kill the game it was checking on.
    """
    try:
        pid = int(pid)
    except (ValueError, TypeError):
        return False

    if sys.platform == "win32":
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        STILL_ACTIVE = 259
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            return (kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) != 0
                    and code.value == STILL_ACTIVE)
        finally:
            kernel32.CloseHandle(handle)

    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def handshake_info():
    """The handshake content plus whether its pid is alive — WITHOUT requiring it to be.

    endpoint() refuses a dead run because a channel question must never be answered by a
    corpse. The arena readers need the opposite: a dead run handshake still names the
    leaked arena that IS its post-mortem image.
    """
    if not os.path.exists(HANDSHAKE):
        return None, "no handshake at " + HANDSHAKE
    try:
        with open(HANDSHAKE) as handle:
            info = json.load(handle)
    except (OSError, ValueError) as error:
        return None, "handshake at " + HANDSHAKE + " is unreadable: " + str(error)

    info["alive"] = alive(info.get("pid"))
    return info, None


def endpoint():
    """The CURRENT game's port, or an error saying precisely which part is missing."""
    if not os.path.exists(HANDSHAKE):
        return None, (
            "no game is publishing a debug channel (no handshake at "
            f"{HANDSHAKE}) — start a run with REFII_DEBUG_PORT=1"
        )
    try:
        with open(HANDSHAKE) as handle:
            info = json.load(handle)
    except (OSError, ValueError) as error:
        return None, f"handshake at {HANDSHAKE} is unreadable: {error}"

    if not alive(info.get("pid")):
        # SIGKILL leaves the file behind. Say so plainly rather than connecting to whatever
        # else may have taken that port in the meantime.
        return None, (
            f"the game that published this channel (pid {info.get('pid')}) is gone — "
            "stale handshake; start a run"
        )

    return info, None


def ask(payload, timeout=5.0):
    """One request, one reply, against the game the handshake names right now.

    A fresh connection and a fresh handshake read every time: this process outlives any number
    of games, and caching either is how a dead run gets mistaken for a live one.
    """
    global _last_boot

    info, error = endpoint()
    if info is None:
        return {"ok": False, "error": error}

    try:
        with socket.create_connection((HOST, info["port"]), timeout=timeout) as s:
            s.sendall((json.dumps(payload) + "\n").encode())
            s.settimeout(timeout)
            chunks = b""
            while b"\n" not in chunks:
                part = s.recv(65536)
                if not part:
                    break
                chunks += part
            if not chunks.strip():
                return {
                    "ok": False,
                    "error": f"pid {info.get('pid')} closed the connection without a reply"
                             + (" — the game has exited" if not alive(info.get("pid"))
                                else " — it is still running"),
                }
            reply = json.loads(chunks.decode().splitlines()[0])
    except (OSError, ValueError, IndexError) as failure:
        return {
            "ok": False,
            "error": f"{failure} — pid {info.get('pid')} publishes port {info.get('port')} "
                     "but did not answer",
        }

    # The answer names who gave it. A different boot id means a different run of the game, and
    # anything concluded from an earlier one no longer applies.
    boot = reply.get("boot")
    if boot is not None:
        if _last_boot is not None and boot != _last_boot:
            reply["restarted"] = True
            reply["note"] = (
                f"the game restarted since the last call (was {_last_boot}, now {boot}) — "
                "earlier screenshots and state are from a different run"
            )
        _last_boot = boot

    return reply
