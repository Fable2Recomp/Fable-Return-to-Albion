"""Assertions against the live game — the primitive UI contract tests are made of."""
from ..channel import ask
from ..registry import tool


def _dig(source, path):
    node = source
    for part in str(path).split("."):
        if isinstance(node, dict) and part in node:
            node = node[part]
        elif isinstance(node, list) and part.isdigit() and int(part) < len(node):
            node = node[int(part)]
        else:
            return None, False
    return node, True


@tool(
    "refii_expect",
    "Assert states through the channel and report pass/fail per check — the regression "
    "primitive. `checks` is a list of {op, path, equals?, contains?}: op is a channel op "
    "(state, frontend, rows, elements ...), path is dotted into its reply "
    "(e.g. 'screen', 'rows.0.label'), and the value must equal `equals` or contain "
    "`contains`. Extra op arguments ride in `args`. ok=false when any check fails.",
    {
        "checks": {
            "type": "array",
            "items": {"type": "object"},
            "description": "The assertions.",
        },
    },
    ["checks"],
)
def expect(args):
    results = []
    passed = True
    replies = {}
    for check in args.get("checks", []):
        op = str(check.get("op", "state"))
        key = op + repr(sorted((check.get("args") or {}).items()))
        if key not in replies:
            replies[key] = ask({"op": op, **(check.get("args") or {})}, timeout=15.0)
        reply = replies[key]
        outcome = {"op": op, "path": check.get("path")}
        if not reply.get("ok"):
            outcome["failed"] = f"op error: {reply.get('error')}"
            passed = False
            results.append(outcome)
            continue
        value, found = _dig(reply, check.get("path", ""))
        outcome["value"] = value
        if not found:
            outcome["failed"] = "path not present"
            passed = False
        elif "equals" in check and str(value) != str(check["equals"]):
            outcome["failed"] = f"wanted {check['equals']!r}"
            passed = False
        elif "contains" in check and str(check["contains"]).lower() not in str(value).lower():
            outcome["failed"] = f"wanted it to contain {check['contains']!r}"
            passed = False
        results.append(outcome)
    return {"ok": passed, "passed": passed, "checks": results}
