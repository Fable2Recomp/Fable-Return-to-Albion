#!/usr/bin/env python3
"""Prepare a PRIVATE module/save/config bundle for a later app-UID transfer.

The game bank directory remains in place; --device-game-root names its eventual
physical Android location. This command performs no ADB or device operations.
It never copies game-derived data into the checkout or a public APK.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path, PurePosixPath
import shutil
import stat

from android_common import inspect_elf, sha256


def physical_android_path(value: str) -> str:
    if not value.startswith("/") or "://" in value or any(p in ("..", ".") for p in value.split("/")):
        raise ValueError("Device paths must be absolute ordinary Android filesystem paths")
    return str(PurePosixPath(value))


def reject_link(path: Path) -> None:
    """Reject the original link entry before resolving away its identity.

    Windows directory junctions are reparse points rather than symlinks. Python
    3.10/3.11 expose the mount-point reparse tag but do not have Path.is_junction.
    """
    junction_query = getattr(path, "is_junction", None)
    if junction_query is not None:
        junction = junction_query()
    else:
        try:
            junction = getattr(path.lstat(), "st_reparse_tag", 0) == getattr(stat, "IO_REPARSE_TAG_MOUNT_POINT", 0xA0000003)
        except (FileNotFoundError, NotADirectoryError):
            junction = False
    if path.is_symlink() or junction:
        raise ValueError("Private copies do not follow symlinks or junctions: " + str(path))


def ordinary_input(path: Path) -> Path:
    """Validate a raw supplied file/directory entry, then normalize its path."""
    reject_link(path)
    return path.resolve()


def validate_save_tree(raw: Path, output: Path) -> Path:
    """Check a separate ordinary save tree before creating any private output."""
    saves = ordinary_input(raw)
    if not saves.is_dir() or output == saves or saves in output.parents or output in saves.parents:
        raise ValueError("Save source must be an ordinary separate directory")
    for path in saves.rglob("*"):
        reject_link(path)
    return saves


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--module", required=True, type=Path)
    parser.add_argument("--save-root", type=Path, help="Optional complete native user_data_root, including account/title/profile hierarchy")
    parser.add_argument("--device-user-root", required=True, help="Actual Godot user data directory reported by the installed app")
    parser.add_argument("--device-game-root", required=True, help="App-readable ordinary directory containing privately supplied game files")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    if output == source or source in output.parents:
        parser.error("Private output must be outside the source checkout")
    if output.exists():
        parser.error("Choose a new output directory; existing private inputs are never overwritten")
    module = ordinary_input(args.module)
    if not module.is_file():
        parser.error("Module must be an ordinary file")
    elf = inspect_elf(module.read_bytes())
    if elf["machine"] != 183 or elf["type"] != 3 or elf["soname"] != "librefii_generated.so":
        parser.error("Expected the matching ARM64 Android generated game module")
    user = physical_android_path(args.device_user_root)
    game = physical_android_path(args.device_game_root)
    saves = validate_save_tree(args.save_root, output) if args.save_root else None
    config = dict(schema=1, game_root=game, skip_movies=True, movie_dwell_seconds=2)
    output.mkdir(parents=True)
    (output / "module").mkdir()
    shutil.copyfile(module, output / "module/librefii_generated.so")
    inputs = {"module/librefii_generated.so": sha256(module)}
    if saves is not None:
        shutil.copytree(saves, output / "imported-save")
        config["save_import_root"] = user + "/imported-save"
        for path in (output / "imported-save").rglob("*"):
            if path.is_file():
                inputs[path.relative_to(output).as_posix()] = sha256(path)
    (output / "android-runtime.json").write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    (output / "PRIVATE-import-manifest.json").write_text(json.dumps(dict(private_game_derived=True,
        device_user_root=user, config=config, sha256=inputs), indent=2), encoding="utf-8")
    print(f"Prepared PRIVATE bundle at {output}; transfer as app UID, preserving originals. No APK/device changed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
