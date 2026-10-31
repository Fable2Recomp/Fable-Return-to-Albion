"""Prepare private PC-generated C++ for Android without changing its inputs.

The pinned PC generator historically omits PPC memory barriers on the assumption
that the host is x86. Restore exact missing barrier sites for ARM64, recognize
the corrected generator output, and preserve matching headers/build metadata.
Output is content-addressed and checked on reuse; ambiguous source is refused.
No image, assets, save or generated module belongs in the distributable APK.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import uuid

SCHEMA = 1
ARM64_GUARD = "defined(__aarch64__) || defined(__arm64__) || defined(_M_ARM64) || defined(_M_ARM64EC)"
LEGACY_GUARD = "defined(__aarch64__) || defined(_M_ARM64)"
FENCE = b"std::atomic_thread_fence(std::memory_order_seq_cst);"
MARKER = b"// Refii checked ARM64 guest memory barriers"
EXACT = re.compile(rb"^([ \t]*)// (sync|lwsync|eieio)[ \t]*(\r?\n|$)")
POSSIBLE = re.compile(rb"^[ \t]*// (sync|lwsync|eieio)\b")
SOURCE_SUFFIXES = {".cpp", ".h", ".cmake"}


def digest(data: bytes) -> str:
    """Return a stable input/output identity without platform path dependence."""
    return hashlib.sha256(data).hexdigest()


def restore_barriers(data: bytes) -> tuple[bytes, dict]:
    """Restore only exact barrier comments and validate existing expansions.

    The no-operand opcodes are emitted as instruction comments by ReXGlue.
    A subsequent different instruction's fence is not part of this expansion.
    Full seq-cst host-memory fences are intentionally conservative on ARM64;
    x86 keeps the prior emitted instruction behavior.
    """
    lines = data.splitlines(keepends=True)
    out = []
    counts = {name: {"inserted": 0, "existing": 0, "guard_upgraded": 0}
              for name in ("sync", "lwsync", "eieio")}
    index = 0
    changed = False
    while index < len(lines):
        line = lines[index]
        match = EXACT.fullmatch(line)
        if match is None:
            if POSSIBLE.match(line):
                raise ValueError("Malformed or unknown barrier instruction comment: " + repr(line))
            out.append(line)
            index += 1
            continue
        indent, opcode, newline = match.groups()
        opcode = opcode.decode("ascii")
        newline = newline or b"\n"
        following = lines[index + 1:index + 4]
        if following and following[0].strip().startswith(b"#if"):
            if (len(following) != 3 or following[1].strip() != FENCE or
                    following[2].strip() != b"#endif"):
                raise ValueError("Ambiguous pre-existing barrier expansion")
            guard = following[0].strip().decode("ascii")
            if guard not in ("#if " + ARM64_GUARD, "#if " + LEGACY_GUARD):
                raise ValueError("Unknown pre-existing ARM barrier guard")
            out.append(line)
            if guard == "#if " + LEGACY_GUARD:
                out.append(("#if " + ARM64_GUARD).encode() + newline)
                out.extend(following[1:])
                counts[opcode]["guard_upgraded"] += 1
                changed = True
            else:
                out.extend(following)
                counts[opcode]["existing"] += 1
            index += 4
            continue
        if following and following[0].strip() == FENCE:
            raise ValueError("Unconditional or ambiguous pre-existing barrier fence")
        out.extend([line if line.endswith(b"\n") else line + newline,
                    ("#if " + ARM64_GUARD).encode() + newline,
                    indent + FENCE + newline, b"#endif" + newline])
        counts[opcode]["inserted"] += 1
        changed = True
        index += 1
    if not changed:
        return data, counts
    newline = b"\r\n" if b"\r\n" in data else b"\n"
    prefix = b"" if MARKER in data else MARKER + newline + b"#include <atomic>" + newline
    return prefix + b"".join(out), counts


def prepare(source: Path, output_root: Path, manifest_path: Path) -> dict:
    """Publish or verify a private source stage; never overwrite an existing stage.

    Validate every input before publication. A partial staging directory remains
    separate on an I/O failure; only a completed directory is renamed into its
    final content-addressed path. Symlinks/junctions and unexpected nested input
    trees are refused so preparation cannot alias the player's original files.
    """
    source = source.resolve()
    output_root = output_root.resolve()
    manifest_path = manifest_path.resolve()
    if source == output_root or source in output_root.parents:
        raise ValueError("Android source output must be separate from the input directory")
    if source == manifest_path or source in manifest_path.parents:
        raise ValueError("Preparation metadata must not modify the input directory")
    for required in ("sources.cmake", "refii_pch.h"):
        if not (source / required).is_file():
            raise ValueError("Generated source is missing " + required)
    if b"#include <atomic>" not in (source / "refii_pch.h").read_bytes():
        raise ValueError("Matching generated PCH must explicitly include <atomic>")
    files = []
    for path in sorted(source.iterdir()):
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            raise ValueError("Generated input must not contain links: " + str(path))
        if path.is_dir():
            raise ValueError("Generated input must be a flat source directory")
        if path.suffix in SOURCE_SUFFIXES:
            files.append(path)
    records = []
    totals = {name: {"inserted": 0, "existing": 0, "guard_upgraded": 0}
              for name in ("sync", "lwsync", "eieio")}
    for path in files:
        original = path.read_bytes()
        if path.suffix == ".cpp":
            output, counts = restore_barriers(original)
            for name in totals:
                for kind in totals[name]:
                    totals[name][kind] += counts[name][kind]
        else:
            output, counts = original, {}
        records.append({"name": path.name, "input_sha256": digest(original),
                        "output_sha256": digest(output), "changed": original != output,
                        "barriers": counts})
    if not any(path.suffix == ".cpp" for path in files):
        raise ValueError("Generated directory contains no C++ translation units")
    policy_sha = digest(Path(__file__).read_bytes())
    identity = digest(json.dumps({"schema": SCHEMA, "policy_sha256": policy_sha,
                                  "records": records}, sort_keys=True).encode())
    destination = output_root / identity[:24]
    report = {"schema_version": SCHEMA, "private_game_derived": True,
              "input_directory": str(source), "prepared_directory": str(destination),
              "identity": identity, "policy_sha256": policy_sha, "arm64_guard": ARM64_GUARD,
              "counts": totals, "files": records,
              "ignored_non_source_files": sorted(path.name for path in source.iterdir()
                                                  if path.suffix not in SOURCE_SUFFIXES)}
    if destination.exists():
        if destination.is_symlink() or getattr(destination, "is_junction", lambda: False)():
            raise ValueError("Prepared source must not be a link")
        stored = json.loads((destination / "android-source-manifest.json").read_text())
        if stored["identity"] != identity or stored["files"] != records:
            raise ValueError("Existing prepared source identity mismatch")
        for record in records:
            if digest((destination / record["name"]).read_bytes()) != record["output_sha256"]:
                raise ValueError("Existing prepared source was modified: " + record["name"])
    else:
        output_root.mkdir(parents=True, exist_ok=True)
        staging = output_root / (".staging-" + uuid.uuid4().hex)
        staging.mkdir()
        for path, record in zip(files, records):
            original = path.read_bytes()
            output = restore_barriers(original)[0] if path.suffix == ".cpp" else original
            if digest(original) != record["input_sha256"] or digest(output) != record["output_sha256"]:
                raise ValueError("Generated input changed while preparing: " + path.name)
            (staging / path.name).write_bytes(output)
        (staging / "android-source-manifest.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        staging.rename(destination)
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_manifest = manifest_path.with_name(manifest_path.name + "." + uuid.uuid4().hex + ".tmp")
    temporary_manifest.write_text(json.dumps(report, indent=2), encoding="utf-8")
    temporary_manifest.replace(manifest_path)
    return report


def main() -> int:
    """Print only the verified source path for Android CMake's configure contract."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()
    report = prepare(args.source, args.output_root, args.manifest)
    print(report["prepared_directory"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
