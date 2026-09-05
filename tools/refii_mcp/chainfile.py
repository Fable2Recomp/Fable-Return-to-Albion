"""The chains.toml loader: verified deref recipes by name. See the file's own header rule —
nothing enters it unverified."""
import os
import tomllib

CHAINS = os.environ.get(
    "REFII_MCP_CHAINS",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "chains.toml"),
)

_loaded = None


def all_chains():
    global _loaded
    if _loaded is None:
        try:
            with open(CHAINS, "rb") as file:
                _loaded = tomllib.load(file)
        except (OSError, tomllib.TOMLDecodeError):
            _loaded = {}
    return _loaded


def get(name):
    """(base_addr, deref_list, note, fields) or None. fields: [{off, type, name}] with type
    in u8/u16/u32/s32/f32/ptr — the typed-dump layer over a verified chain."""
    entry = all_chains().get(name)
    if not isinstance(entry, dict):
        return None
    try:
        base = int(str(entry["base"]), 16)
        deref = [int(o) for o in entry.get("deref", [])]
    except (KeyError, ValueError, TypeError):
        return None
    fields = [f for f in entry.get("fields", []) if isinstance(f, dict)]
    return base, deref, str(entry.get("note", "")), fields
