#!/usr/bin/env python3

from __future__ import annotations

import argparse
import csv
import shutil
import subprocess
import sys
from pathlib import Path


LUA_SIGNATURE = b"\x1bLua"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Decompile the extracted Fable 2 Lua workspace into a readable mirror."
    )
    parser.add_argument(
        "workspace",
        nargs="?",
        default="out/build/linux-amd64-debug/assets/game/lua",
        help="Path to the extracted Lua workspace root.",
    )
    parser.add_argument(
        "--out",
        help="Output directory for readable scripts. Defaults to <workspace>/decompiled.",
    )
    parser.add_argument(
        "--unluac",
        default="tools/unluac.jar",
        help="Path to unluac.jar. When absent, bytecode files are inventoried but not decompiled.",
    )
    parser.add_argument(
        "--include-retail",
        action="store_true",
        help="Also try to decompile stripped gamescripts_r bytecode.",
    )
    return parser.parse_args()


def is_lua_bytecode(data: bytes) -> bool:
    return len(data) >= 12 and data.startswith(LUA_SIGNATURE)


def decode_source_name(data: bytes) -> str:
    if len(data) < 16 or not is_lua_bytecode(data):
        return ""

    version = data[4]
    endianness = data[6]
    size_t_size = data[8]
    if version != 0x51 or endianness != 0x01 or size_t_size != 0x04:
        return ""

    source_size = int.from_bytes(data[12:16], "little")
    if source_size <= 1 or 16 + source_size > len(data):
        return ""

    return data[16 : 16 + source_size - 1].decode("utf-8", errors="replace")


def should_process(relative_path: Path, include_retail: bool) -> bool:
    normalized = relative_path.as_posix()
    if normalized.startswith("gamescripts_r/") and not include_retail:
        return False
    return normalized.endswith(".lua")


def run_unluac(unluac_jar: Path, input_path: Path) -> tuple[bool, str]:
    result = subprocess.run(
        ["java", "-jar", str(unluac_jar), str(input_path)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    return result.returncode == 0, result.stdout


def ensure_parent(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def main() -> int:
    args = parse_args()
    workspace = Path(args.workspace).resolve()
    output_root = Path(args.out).resolve() if args.out else workspace / "decompiled"
    unluac_jar = Path(args.unluac).resolve()

    if not workspace.is_dir():
        print(f"Workspace not found: {workspace}", file=sys.stderr)
        return 1

    has_unluac = unluac_jar.is_file()
    manifest_rows: list[dict[str, str]] = []
    processed = 0
    decompiled = 0
    copied_plaintext = 0
    skipped = 0

    for input_path in sorted(workspace.rglob("*.lua")):
        relative = input_path.relative_to(workspace)
        if not should_process(relative, args.include_retail):
            continue

        processed += 1
        data = input_path.read_bytes()
        source_name = decode_source_name(data)
        output_path = output_root / relative
        status = "plaintext"
        note = ""

        if is_lua_bytecode(data):
            status = "bytecode"
            if has_unluac:
                ok, decompiled_text = run_unluac(unluac_jar, input_path)
                if ok and decompiled_text.strip():
                    ensure_parent(output_path)
                    output_path.write_text(decompiled_text, encoding="utf-8")
                    decompiled += 1
                    status = "decompiled"
                else:
                    skipped += 1
                    note = "unluac failed"
            else:
                skipped += 1
                note = "missing unluac.jar"
        else:
            ensure_parent(output_path)
            shutil.copyfile(input_path, output_path)
            copied_plaintext += 1

        manifest_rows.append(
            {
                "status": status,
                "input": relative.as_posix(),
                "output": output_path.relative_to(output_root).as_posix()
                if output_path.exists()
                else "",
                "source_name": source_name,
                "note": note,
            }
        )

    manifest_path = output_root / "_manifest.tsv"
    ensure_parent(manifest_path)
    with manifest_path.open("w", encoding="utf-8", newline="") as manifest_file:
        writer = csv.DictWriter(
            manifest_file,
            fieldnames=["status", "input", "output", "source_name", "note"],
            delimiter="\t",
        )
        writer.writeheader()
        writer.writerows(manifest_rows)

    print(f"Workspace: {workspace}")
    print(f"Output:    {output_root}")
    print(f"Files:     {processed}")
    print(f"Decompiled:{decompiled}")
    print(f"Copied:    {copied_plaintext}")
    print(f"Skipped:   {skipped}")
    print(f"Manifest:  {manifest_path}")
    if not has_unluac:
        print(f"Missing decompiler jar: {unluac_jar}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
