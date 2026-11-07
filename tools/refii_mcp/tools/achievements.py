"""Achievements: the state of the whole pipeline, and the levers to exercise it.

The pipeline has four joints and each has failed separately: the guest writes through XAM
(`XGIUserWriteAchievements` in the guest log), the native seam holds the write, the bridge hands
it to Godot, and Godot owns the store, its file, the toast and the page. `guestWritesSeen` is
the count that crossed the second and third; a guest log full of writes beside a zero here is
a seam that is not crossing, which is what two copies of the seam looked like.
"""
import glob
import os

from ..channel import ask
from ..registry import tool


def _guest_writes():
    """The guest's own record of its writes this boot, newest last — the far side of the seam."""
    info = ask({"op": "ping"})
    if not info.get("ok"):
        return None
    pid = info.get("pid")
    try:
        exe_dir = os.path.dirname(os.readlink(f"/proc/{pid}/exe"))
    except OSError:
        return None
    lines = []
    # Each title's guest logs sit in its own folder (logs/Fable2/refii_*, logs/Fable3/refiii_*).
    found = glob.glob(os.path.join(exe_dir, "logs", "*", "refii*_*.log*"))
    for path in sorted(found, key=os.path.getmtime)[-3:]:
        try:
            with open(path, errors="replace") as handle:
                lines += [line.strip() for line in handle if "XGIUserWriteAchievements: id=" in line]
        except OSError:
            continue
    return lines[-8:]


@tool(
    "refii_achievements",
    "The achievement system end to end, and the levers to test it without playing for one. "
    "do='state' (default): store path and whether the file exists, unlocked count and "
    "gamerscore, how many GUEST writes have crossed the bridge this session, the toast's state, "
    "and the guest log's own XGIUserWriteAchievements lines for comparison. do='list': that plus "
    "every achievement with id, name, gamerscore, unlocked, when, and whether it has an icon. "
    "do='unlock' id=N: unlocks one by the SAME path a guest write takes — store, file, toast "
    "queue, and the set stated back down to the guest's enumerator. do='toast' id=N: shows the "
    "toast alone, unlocking nothing (pair with refii_shot; it lasts ten seconds, the "
    "description phase starts at five). do='lock' id=N re-locks one and do='reset' re-locks "
    "all, so an unlock can be seen again. unlock / lock / reset WRITE the player's achievements "
    "file; toast and dial do not. do='dial' name=X value=N sets one of the toast's LOOK dials "
    "live — every reply lists them with their current values under `dials` (sizes are HUD "
    "canvas units). There is ONE look, the player's choice: brown panel, gold rule, gold over "
    "cream; the dials tune it, they do not switch it. Fire a toast, shoot it, turn a dial, fire "
    "again: no rebuild. A shot stalls the frame and the toast's clock with it, so "
    "time a capture by the reply's `toast` age, never by the wall clock.",
    {
        "do": {"type": "string",
               "enum": ["state", "list", "unlock", "toast", "lock", "reset", "dial"]},
        "id": {"type": "integer", "description": "Achievement id, for unlock / toast / lock."},
        "name": {"type": "string", "description": "Dial name, for do='dial'."},
        "value": {"type": "number", "description": "Dial value, for do='dial'."},
    },
)
def achievements(args):
    verb = str(args.get("do") or "state")
    request = {"op": "achievements", "do": verb}
    if verb in ("unlock", "toast", "lock"):
        if args.get("id") is None:
            return {"ok": False, "error": f"'{verb}' needs an id — do='list' names them"}
        request["id"] = int(args["id"])
    if verb == "dial":
        if args.get("name") is None or args.get("value") is None:
            return {"ok": False, "error": "'dial' needs a name and a value — do='state' lists the dials"}
        request["name"] = str(args["name"])
        request["value"] = float(args["value"])
    reply = ask(request, timeout=15.0)
    if reply.get("ok") and verb in ("state", "list"):
        reply["guestLogWrites"] = _guest_writes()
    return reply
