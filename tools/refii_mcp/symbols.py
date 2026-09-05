"""Symbolization: raw guest addresses become IDA's names, offline.

The tables are one-time exports from the IDA database (tools/cache/guest_symbols.tsv and
guest_globals.tsv — start\tend\tname and addr\tname, bare uppercase hex). They are OUR
analysis metadata, refreshed by re-running the export when renames matter; a stale name is
still the right function, because code addresses do not move in a fixed XEX image.

Every consumer goes through annotate()/resolve() so an address prints the same way
everywhere: "82170CCC sub_82170050+0x7C".
"""
import bisect
import os

SYMBOLS = os.environ.get(
    "REFII_MCP_SYMBOLS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                 "cache", "guest_symbols.tsv"),
)
GLOBALS = os.environ.get(
    "REFII_MCP_GLOBALS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                 "cache", "guest_globals.tsv"),
)

_starts = None   # sorted function starts
_ends = []       # end for _starts[i]
_names = []      # name for _starts[i]
_by_name = {}
_globals = {}    # addr -> name
_global_addrs = []


def _load():
    global _starts
    if _starts is not None:
        return
    _starts = []
    try:
        with open(SYMBOLS) as table:
            rows = []
            for line in table:
                parts = line.rstrip("\n").split("\t")
                if len(parts) == 3:
                    try:
                        rows.append((int(parts[0], 16), int(parts[1], 16), parts[2]))
                    except ValueError:
                        continue
            rows.sort()
            for start, end, name in rows:
                _starts.append(start)
                _ends.append(end)
                _names.append(name)
                _by_name[name] = start
    except OSError:
        pass
    try:
        with open(GLOBALS) as table:
            for line in table:
                parts = line.rstrip("\n").split("\t")
                if len(parts) == 2:
                    try:
                        _globals[int(parts[0], 16)] = parts[1]
                    except ValueError:
                        continue
        _global_addrs.extend(sorted(_globals))
    except OSError:
        pass


def loaded():
    _load()
    return len(_starts) > 0


def resolve(addr):
    """The symbol at `addr`, or None: exact global, containing function (+offset), or None."""
    _load()
    name = _globals.get(addr)
    if name is not None:
        return name
    index = bisect.bisect_right(_starts, addr) - 1
    if index >= 0 and _starts[index] <= addr < _ends[index]:
        offset = addr - _starts[index]
        return _names[index] if offset == 0 else f"{_names[index]}+0x{offset:X}"
    return None


def lookup(name):
    """A name's address: function start, or a global. None when unknown."""
    _load()
    if name in _by_name:
        return _by_name[name]
    for addr, held in _globals.items():
        if held == name:
            return addr
    return None


def annotate(addresses):
    """hex-string list -> 'ADDR name+off' where known, the bare hex where not."""
    _load()
    out = []
    for text in addresses:
        try:
            symbol = resolve(int(text, 16))
        except (ValueError, TypeError):
            symbol = None
        out.append(f"{text} {symbol}" if symbol else text)
    return out
