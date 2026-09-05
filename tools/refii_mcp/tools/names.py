"""FNV hashes and their names, offline. Half of every guest readout is a hash; this answers
it without a run."""
import glob
import os
import struct
import zlib

from ..registry import tool


def fnv1(text):
    value = 2166136261
    for byte in text.encode("utf-8"):
        value = (value * 16777619) & 0xFFFFFFFF
        value ^= byte
    return value


def fnv1a(text):
    value = 2166136261
    for byte in text.encode("utf-8"):
        value ^= byte
        value = (value * 16777619) & 0xFFFFFFFF
    return value


def _find_babel(culture):
    roots = [os.environ.get("REFII_GAME_DATA", "")]
    tree = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__)))))
    # The installed package is where game data really lives; build dirs are usually empty.
    roots += sorted(glob.glob(os.path.join(tree, "out", "package", "*", "assets", "game", "data")),
                    key=os.path.getmtime, reverse=True)
    roots += glob.glob(os.path.join(tree, "out", "build", "*", "bin", "assets", "game", "data"))
    for root in roots:
        if not root:
            continue
        hits = glob.glob(os.path.join(root, "language", culture or "*", "text", "book.babel"))
        if hits:
            return hits[0]
    return None


def _babel_get(path, key):
    """The string behind one FNV-1 key, straight out of book.babel — the same decode
    Engine_BabelDatabase does. Raw-stored blocks (comp == unc) never go through zlib."""
    data = open(path, "rb").read()
    at = 0

    def u32():
        nonlocal at
        value = struct.unpack_from(">I", data, at)[0]
        at += 4
        return value

    if u32() != 0x5B010000:
        return None
    count = u32()
    records = {}
    for _ in range(count):
        record_key = u32()
        records[record_key] = (u32(), u32())
    block_count = u32()
    blocks = {}
    for _ in range(block_count):
        block_id = u32()
        comp = u32()
        unc = u32()
        blocks[block_id] = (at, comp, unc)
        at += comp

    if key not in records:
        return None
    block_id, offset = records[key]
    if block_id not in blocks:
        return None
    start, comp, unc = blocks[block_id]
    block = data[start:start + comp] if comp == unc else \
        zlib.decompressobj().decompress(data[start:start + comp], unc)
    chars = struct.unpack_from(">I", block, offset)[0]
    return block[offset + 4: offset + 4 + (chars - 1) * 2].decode("utf-16-be", errors="replace")


@tool(
    "refii_name",
    "FNV hashes both ways, offline against the local game data. Give `name` for its FNV-1 "
    "and FNV-1a (Babel keys are FNV-1 case-sensitive; audio ids FNV-1 of the UPPERCASED "
    "name) plus the Babel string when the tag exists. Give `hash` (hex) for the Babel "
    "string behind it, and — with `wordlist`, a file of candidate names — the reverse "
    "lookup FNV cannot do alone.",
    {
        "name": {"type": "string", "description": "A tag or entity name to hash."},
        "hash": {"type": "string", "description": "A hash (hex) to look up."},
        "wordlist": {"type": "string",
                     "description": "With hash: file of candidate names, one per line."},
        "culture": {"type": "string", "description": "Babel culture (default en-uk)."},
    },
)
def name(args):
    babel = _find_babel(str(args.get("culture", "en-uk")))
    if args.get("name"):
        text = str(args["name"])
        reply = {"ok": True, "name": text,
                 "fnv1": f"{fnv1(text):08X}", "fnv1a": f"{fnv1a(text):08X}",
                 "fnv1Upper": f"{fnv1(text.upper()):08X}"}
        if babel:
            reply["babel"] = _babel_get(babel, fnv1(text))
            reply["babelFile"] = babel
        return reply

    if args.get("hash"):
        try:
            key = int(str(args["hash"]).removeprefix("0x"), 16)
        except ValueError:
            return {"ok": False, "error": f"{args['hash']!r} is not hex"}
        reply = {"ok": True, "hash": f"{key:08X}"}
        if babel:
            reply["babel"] = _babel_get(babel, key)
            reply["babelFile"] = babel
        if args.get("wordlist"):
            try:
                matches = []
                with open(str(args["wordlist"])) as words:
                    for line in words:
                        word = line.strip()
                        if word and (fnv1(word) == key or fnv1a(word) == key or
                                     fnv1(word.upper()) == key):
                            matches.append(word)
                            if len(matches) >= 20:
                                break
                reply["wordlistMatches"] = matches
            except OSError as error:
                reply["wordlistError"] = str(error)
        return reply

    return {"ok": False, "error": "give name or hash"}
