#!/usr/bin/env python3
"""Build Android export templates from Godot 4.7.1-stable with this folder's patches.

    build-templates.py --godot-source DIR --official-templates DIR --out DIR [--jobs N]

--godot-source is a Godot 4.7.1-stable source tree (a git checkout of the tag, with the Swappy
frame-pacing libraries installed: misc/scripts/install_swappy_android.py). The patches here are
applied to it in name order; a tree that already carries them is left as it is. --official-templates
is the official 4.7.1 .NET export templates folder (android_debug.apk, android_release.apk,
android_source.zip, version.txt). --out receives a copy of it whose android_source.zip carries the
patched arm64 libgodot_android.so in libs/debug/godot-lib.template_debug.aar; everything else stays
the official files. tools/android/export-apk.py exports with --export-debug, so the debug library
is the only one the game ships.

The build needs SCons on PATH (or --scons) and ANDROID_HOME pointing at an SDK with NDK r29.
"""
import argparse, io, os, shutil, subprocess, sys, zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
AAR = "libs/debug/godot-lib.template_debug.aar"
SO = "jni/arm64-v8a/libgodot_android.so"


def apply_patches(source: Path) -> None:
    for patch in sorted(HERE.glob("*.diff")):
        reverse = subprocess.run(["git", "apply", "--reverse", "--check", str(patch)], cwd=source,
                                 capture_output=True)
        if reverse.returncode == 0:
            print(f"already applied: {patch.name}")
            continue
        subprocess.run(["git", "apply", "--check", str(patch)], cwd=source, check=True)
        subprocess.run(["git", "apply", str(patch)], cwd=source, check=True)
        print(f"applied: {patch.name}")


def build(source: Path, scons: str, jobs: int) -> Path:
    subprocess.run([scons, "platform=android", "arch=arm64", "target=template_debug",
                    "module_mono_enabled=yes", "production=yes", "swappy=yes", f"-j{jobs}"],
                   cwd=source, check=True)
    lib = source / "platform/android/java/lib/libs/debug/arm64-v8a/libgodot_android.so"
    if not lib.is_file():
        raise SystemExit(f"no library at {lib}")
    return lib


def wrap(lib: Path, official: Path, out: Path) -> None:
    """A copy of the official templates with the debug aar's arm64 library replaced."""
    version = (official / "version.txt").read_text(encoding="utf-8").strip()
    if version != "4.7.1.stable.mono":
        raise SystemExit(f"expected the official 4.7.1 .NET templates, found {version}")
    out.mkdir(parents=True, exist_ok=True)
    for name in os.listdir(official):
        if name != "android_source.zip":
            shutil.copy2(official / name, out / name)
    payload = lib.read_bytes()
    with zipfile.ZipFile(official / "android_source.zip") as zin, \
         zipfile.ZipFile(out / "android_source.zip", "w") as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if item.filename == AAR:
                buf = io.BytesIO()
                with zipfile.ZipFile(io.BytesIO(data)) as ain, zipfile.ZipFile(buf, "w") as aout:
                    for entry in ain.infolist():
                        aout.writestr(entry, payload if entry.filename == SO else ain.read(entry.filename))
                data = buf.getvalue()
            zout.writestr(item, data)
    print(f"templates -> {out}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--godot-source", type=Path, required=True)
    parser.add_argument("--official-templates", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--scons", default="scons")
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()
    apply_patches(args.godot_source)
    wrap(build(args.godot_source, args.scons, args.jobs), args.official_templates, args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
