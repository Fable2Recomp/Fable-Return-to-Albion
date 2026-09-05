"""Is there a game, and is the guest up inside it."""
from ..channel import HANDSHAKE, ask, endpoint
from ..registry import tool


@tool(
    "refii_ping",
    "Whether a run is up and answering RIGHT NOW, and whether the recompiled guest is up "
    "inside it — with the pid, the boot id and how long it has been running. The first call "
    "to make when anything else says 'no manager': a stale handshake, a game still in codegen "
    "and a game parked on the title all look alike until this distinguishes them.",
)
def ping(_args):
    info, error = endpoint()
    reply = ask({"op": "ping"})
    if info is not None:
        reply["handshake"] = {"path": HANDSHAKE, "pid": info.get("pid"),
                              "port": info.get("port"), "started": info.get("started")}
    elif error:
        reply["handshake"] = {"path": HANDSHAKE, "error": error}
    return reply
