import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "src/launcher/src/core/runtime_settings.cpp"
OUT = ROOT / "Godot/src/Core/RuntimeCVars.generated.cs"

TYPE_MAP = {
    "Bool": "Bool",
    "Int": "Int",
    "Double": "Double",
    "String": "String",
}

def split_fields(body: str):
    fields, current, depth, i = [], [], 0, 0
    while i < len(body):
        ch = body[i]
        if ch == '"':
            current.append(ch)
            i += 1
            while i < len(body):
                current.append(body[i])
                if body[i] == "\\":
                    i += 1
                    if i < len(body):
                        current.append(body[i])
                elif body[i] == '"':
                    break
                i += 1
            i += 1
            continue
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == "," and depth == 0:
            fields.append("".join(current).strip())
            current = []
            i += 1
            continue
        current.append(ch)
        i += 1
    if "".join(current).strip():
        fields.append("".join(current).strip())
    return fields

def cpp_string_to_csharp(literal: str) -> str:
    literal = literal.strip()
    if literal == "nullptr":
        return "null"
    if not (literal.startswith('"') and literal.endswith('"')):
        raise ValueError("not a string literal: " + literal)
    inner = literal[1:-1]
    return '"' + inner + '"'

IS_WINDOWS = True
HAS_FIDELITYFX = False

def resolve_conditionals(lines):
    out, skipping, seen_taken = [], False, []
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("#if"):
            taken = HAS_FIDELITYFX if "FIDELITYFX" in stripped else True
            seen_taken.append(taken)
            skipping = not taken
            continue
        if stripped.startswith("#else"):
            if seen_taken:
                skipping = seen_taken[-1]
            continue
        if stripped.startswith("#endif"):
            if seen_taken:
                seen_taken.pop()
            skipping = False
            continue
        if not skipping:
            out.append(line)
    return out

def iter_definition_bodies(text: str):
    lines = resolve_conditionals(text.splitlines())
    buffer, depth = [], 0
    for line in lines:
        stripped = line.strip()
        if not buffer and not stripped.startswith('{"'):
            continue
        buffer.append(stripped)
        depth += stripped.count("{") - stripped.count("}")
        if depth <= 0:
            joined = " ".join(buffer).strip().rstrip(",")
            if joined.startswith("{") and joined.endswith("}"):
                yield joined[1:-1]
            buffer, depth = [], 0

def main() -> int:
    text = SRC.read_text(encoding="utf-8", errors="surrogateescape")

    entries = []
    for body in iter_definition_bodies(text):
        if "RuntimeCVarType::" not in body:
            continue
        fields = split_fields(body)
        if len(fields) < 5:
            continue

        name = cpp_string_to_csharp(fields[0])
        category = cpp_string_to_csharp(fields[1])
        type_match = re.search(r"RuntimeCVarType::(\w+)", fields[2])
        if not type_match:
            continue
        cvar_type = TYPE_MAP[type_match.group(1)]
        default_value = cpp_string_to_csharp(fields[3])
        description = cpp_string_to_csharp(fields[4])
        allowed = cpp_string_to_csharp(fields[5]) if len(fields) > 5 else "null"

        entries.append((name, category, cvar_type, default_value, description, allowed))

    if not entries:
        print("ERROR: parsed zero definitions from " + str(SRC), file=sys.stderr)
        return 1

    lines = [
        "using System.Collections.Generic;",
        "",
        "namespace Refii.Launcher.Core;",
        "",
        "public static class RuntimeCVars",
        "{",
        "    public const int SourceDefinitionCount = %d;" % len(entries),
        "",
        "    public static readonly IReadOnlyList<RuntimeCVarDefinition> Definitions =",
        "        new List<RuntimeCVarDefinition>",
        "        {",
    ]
    for name, category, cvar_type, default_value, description, allowed in entries:
        lines.append(
            "            new(%s, %s, RuntimeCVarType.%s, %s, %s, %s),"
            % (name, category, cvar_type, default_value, description, allowed)
        )
    lines += ["        };", "}", ""]

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print("wrote %s (%d definitions)" % (OUT.relative_to(ROOT), len(entries)))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
