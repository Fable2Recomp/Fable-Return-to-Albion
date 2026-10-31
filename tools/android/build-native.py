#!/usr/bin/env python3
"""Cross-build the redistributable Android runtime and optional private game module.

Requires a fully initialized matching ReXGlue SDK, NDK r28c, CMake and Ninja.
No ADB, downloads, signing, installation, or asset distribution occurs here.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time


SYSTEM_LIBRARIES = frozenset({
    "libc.so", "libm.so", "libdl.so", "liblog.so", "libandroid.so", "libz.so",
    "libvulkan.so", "libEGL.so", "libGLESv1_CM.so", "libGLESv2.so", "libGLESv3.so",
    "libOpenSLES.so", "libaaudio.so", "libnativewindow.so", "libjnigraphics.so",
    "libmediandk.so", "libcamera2ndk.so"})


def sha256(path: Path) -> str:
    """Hash an input without loading a large generated binary into memory."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def toolchain_bin(ndk: Path) -> Path:
    """Locate the NDK's host tools without baking in a developer machine path."""
    prebuilt = ndk / "toolchains/llvm/prebuilt"
    candidates = [path / "bin" for path in prebuilt.iterdir() if (path / "bin").is_dir()]
    if len(candidates) != 1:
        raise ValueError(f"Expected one NDK host toolchain below {prebuilt}, found {candidates}")
    return candidates[0]


def executable(directory: Path, name: str) -> Path:
    """Return a host executable with its platform extension."""
    return directory / (name + (".exe" if os.name == "nt" else ""))


def elf_info(readelf: Path, library: Path) -> dict:
    """Read ELF architecture, SONAME, dependencies and page alignment."""
    text = subprocess.check_output([str(readelf), "-h", "-d", "-l", "--wide", str(library)], text=True)
    soname = re.search(r"\(SONAME\).*?\[([^]]+)\]", text)
    alignments = [int(line.split()[-1], 16) for line in text.splitlines()
                  if line.strip().startswith("LOAD ")]
    if "AArch64" not in text or not alignments or min(alignments) < 16384:
        raise ValueError(f"{library} is not a 16-KiB-aligned ARM64 ELF")
    return {"soname": soname.group(1) if soname else library.name,
            "needed": re.findall(r"\(NEEDED\).*?\[([^]]+)\]", text),
            "load_alignment": min(alignments)}


def run(command: list[str], log: Path, commands: list, cwd: Path | None = None,
        library_dirs: list[Path] | None = None, extra_env: dict[str, str] | None = None) -> None:
    """Run a bounded build phase with an exact command and durable output log."""
    commands.append({"argv": command, "cwd": str(cwd) if cwd else None, "log": str(log),
                     "library_dirs": [str(path) for path in library_dirs or []], "env": dict(extra_env or {})})
    environment = dict(os.environ, **extra_env) if extra_env else None
    if library_dirs:
        environment = dict(environment or os.environ)
        variable = "PATH" if os.name == "nt" else "LD_LIBRARY_PATH"
        environment[variable] = os.pathsep.join(map(str, library_dirs)) + os.pathsep + environment.get(variable, "")
    print(f"Running {Path(command[0]).name}; output: {log}", flush=True)
    with log.open("w", encoding="utf-8") as stream:
        result = subprocess.run(command, cwd=cwd, env=environment, stdout=stream, stderr=subprocess.STDOUT,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}); see {log}")


def generate_private(source: Path, game: Path, generator: Path, output: Path,
                     version: str, commands: list) -> Path:
    """Prepare an isolated private XEX copy, then run the matching PC generator."""
    private = output / "private-codegen"
    if private.exists():
        raise ValueError("Private codegen output already exists; choose a new output directory")
    inputs = private / "input"
    inputs.mkdir(parents=True)
    for name in ("default.xex", "default.xexp"):
        path = game / name
        if path.is_file():
            shutil.copyfile(path, inputs / name)
    if not (inputs / "default.xex").is_file():
        raise ValueError("Game directory must contain the privately supplied default.xex")
    # Since the title split the version configs are under Fable2/ (pack-phone-bundle.py reads them the same way).
    versions = source / "Fable2" / "versions" if (source / "Fable2" / "versions").is_dir() else source / "versions"
    config_root = versions / version
    shutil.copyfile(config_root / "refii_config.toml", private / "refii_config.toml")
    manifest = (config_root / "refii_manifest.toml").read_text(encoding="utf-8")
    manifest = re.sub(r"(?m)^file_path\s*=.*$", 'file_path = ' + json.dumps((inputs / "default.xex").as_posix()), manifest)
    manifest = re.sub(r"(?m)^out_directory_path\s*=.*$", 'out_directory_path = ' + json.dumps((private / "generated").as_posix()), manifest)
    manifest_path = private / "refii_manifest.toml"
    manifest_path.write_text(manifest, encoding="utf-8")
    # Desktop packages keep the generator's runtime beside bin/recompile/lib
    # and the application bin. Scope lookup to this child, preserving host PATH.
    library_dirs = [path for path in (generator.parent, generator.parent.parent / "lib",
                                     generator.parent.parent.parent) if path.is_dir()]
    run([str(generator), "--force", "--log-file", str(private / "codegen-tool.log"),
         "codegen", str(manifest_path)], private / "codegen.log", commands, private, library_dirs)
    generated = private / "generated"
    # Matching older desktop generators put the shared declarations in init.h
    # without emitting a separate PCH. Keep those exact declarations and provide
    # only the PCH entry point expected by Android's guarded source preparation.
    pch = generated / "refii_pch.h"
    if not pch.exists() and (generated / "refii_init.h").is_file():
        legacy_weak = ''
        if 'REX_WEAK_FUNC' in (generated / "refii_init.h").read_text(encoding="utf-8"):
            # The pinned runtime's current generator uses this same weak/noinline
            # declaration directly. Supply its spelling for older desktop output
            # without changing any generated function body or original-entry alias.
            legacy_weak = '#ifndef REX_WEAK_FUNC\n#define REX_WEAK_FUNC(name) __attribute__((weak, noinline)) extern "C" REX_FUNC(name)\n#endif\n'
        pch.write_text('#pragma once\n#include <atomic>\n' + legacy_weak + '#include "refii_init.h"\n', encoding="utf-8")
    return generated


def stage_closure(build: Path, destination: Path, ndk_bin: Path) -> list[dict]:
    """Stage the host bridge's full dependency closure, excluding private modules."""
    readelf = executable(ndk_bin, "llvm-readelf")
    by_name: dict[str, Path] = {}
    for library in build.rglob("*.so"):
        if "private-module" in library.parts or "selftest" in library.name:
            continue
        previous = by_name.get(library.name)
        if previous and sha256(previous) != sha256(library):
            raise ValueError(f"Ambiguous native output {library.name}: {previous}, {library}")
        by_name[library.name] = library
    libcxx = ndk_bin.parent / "sysroot/usr/lib/aarch64-linux-android/libc++_shared.so"
    by_name[libcxx.name] = libcxx
    # Lua is a payload dependency even when the host bridge has no direct
    # DT_NEEDED edge to it. Bink/XMA are loaded through managed P/Invoke.
    pending = ["librefii_hostbridge.so", "librefiii_hostbridge.so", "librefii_lua.so",
               "librefii_binkdecode.so", "librefii_xmadecode.so"]
    staged = {}
    destination.mkdir(parents=True, exist_ok=True)
    while pending:
        name = pending.pop()
        if name in staged or name in SYSTEM_LIBRARIES:
            continue
        if name not in by_name:
            raise ValueError(f"Unresolved native dependency: {name}")
        library = by_name[name]
        info = elf_info(readelf, library)
        if info["soname"] != name:
            raise ValueError(f"Library basename must match SONAME: {library}: {info['soname']}")
        path = destination / name
        shutil.copyfile(library, path)
        staged[name] = {"path": "native/" + name, "sha256": sha256(path),
                        "bytes": path.stat().st_size, **info}
        pending.extend(info["needed"])
    return [staged[name] for name in sorted(staged)]


def verify_payload_objects(build: Path, ndk_bin: Path) -> dict:
    """Check every generated/hook ELF for the shared mutex bridge contract."""
    paths = [Path(line) for line in (build / "android-private-object-list.txt")
             .read_text(encoding="utf-8").splitlines() if line]
    if not paths or any(path.suffix != ".o" or not path.is_file() for path in paths):
        raise ValueError("Private module object manifest is missing or contains compiler state")
    imports = []
    nm = executable(ndk_bin, "llvm-nm")
    # Stay below Windows' process command-line limit with long external paths.
    for offset in range(0, len(paths), 8):
        imports.append(subprocess.check_output([str(nm), "--undefined-only", "-C",
            *map(str, paths[offset:offset + 8])], text=True))
    symbols = "\n".join(imports)
    if re.search(r"std::.*recursive_mutex::(?:lock|unlock|try_lock)\(", symbols):
        raise ValueError("Private payload imports a raw recursive_mutex operation; ABI guard failed")
    if "refii_bridge_rex_global_critical_region_lock" not in symbols:
        raise ValueError("Private payload does not import the shared runtime critical-region bridge")
    return {"object_count": len(paths), "no_raw_recursive_mutex_imports": True,
            "shared_critical_region_bridge_imported": True}


def main() -> int:
    """Build outside the source checkout and write a package-consumable manifest."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ndk", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--sdk-source", type=Path)
    parser.add_argument("--zlib-source", type=Path)
    parser.add_argument("--host-codegen", required=True, type=Path)
    parser.add_argument("--generated-dir", type=Path)
    parser.add_argument("--game-directory", type=Path)
    parser.add_argument("--game-version", choices=("Fable2", "Fable2TU1"), default="Fable2TU1")
    parser.add_argument("--api", type=int, default=31)
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--cmake", default="cmake")
    parser.add_argument("--ninja", default="ninja")
    parser.add_argument("--selftests", action="store_true")
    parser.add_argument("--configure-only", action="store_true")
    parser.add_argument("--rexnet", action="store_true",
        help="Build the RexNet netplay module for co-op (needs rustup's aarch64-linux-android target)")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    if output == source or source in output.parents:
        parser.error("Use an output directory outside the source checkout to protect private sources")
    if args.api < 31:
        parser.error("The verified Android API floor is 31")
    if args.generated_dir and args.game_directory:
        parser.error("Choose --generated-dir or --game-directory")
    ndk = args.ndk.resolve()
    ndk_bin = toolchain_bin(ndk)
    sdk = (args.sdk_source or source / "rexglue-net/rexglue-sdk").resolve()
    generator = args.host_codegen.resolve()
    for path in (sdk / "CMakeLists.txt", ndk / "build/cmake/android.toolchain.cmake", generator):
        if not path.is_file():
            parser.error(f"Required build input not found: {path}")
    output.mkdir(parents=True, exist_ok=True)
    commands: list = []
    started = time.monotonic()
    try:
        generated = args.generated_dir.resolve() if args.generated_dir else None
        if args.game_directory:
            generated = generate_private(source, args.game_directory.resolve(), generator,
                                         output, args.game_version, commands)
        build = output / "build"
        configure = [args.cmake, "-S", str(source), "-B", str(build), "-G", "Ninja",
            f"-DCMAKE_MAKE_PROGRAM={args.ninja}",
            f"-DCMAKE_TOOLCHAIN_FILE={ndk / 'build/cmake/android.toolchain.cmake'}",
            "-DANDROID_ABI=arm64-v8a", f"-DANDROID_PLATFORM=android-{args.api}",
            "-DANDROID_STL=c++_shared", "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON", f"-DREFII_REXGLUE_SDK_SOURCE_DIR={sdk}",
            f"-DREFII_ANDROID_HOST_CODEGEN={generator}",
            "-DREFII_STAGE_GODOT_BUILD=OFF", "-DREFII_BUILD_GENERATED_PAYLOAD=OFF",
            "-DREFII_BUILD_SHADER_PIPELINE=OFF", "-DREFII_BUILD_TOOLS=OFF",
            "-DREFII_BUILD_EXTRACTOR=OFF", f"-DREXGLUE_ENABLE_REXNET={'ON' if args.rexnet else 'OFF'}",
            "-DREXGLUE_ENABLE_TRACY=OFF", "-DREXGLUE_ENABLE_PERF_COUNTERS=ON",
            f"-DPython3_EXECUTABLE={sys.executable}", f"-DPYTHON_EXECUTABLE={sys.executable}",
            f"-DREFII_ANDROID_BUILD_SELFTESTS={'ON' if args.selftests else 'OFF'}",
            f"-DREFII_GAME_VERSION={args.game_version}"]
        if generated:
            configure.append(f"-DREFII_ANDROID_GENERATED_DIR={generated}")
        if args.rexnet:
            configure.append("-DRust_CARGO_TARGET=aarch64-linux-android")
        if args.zlib_source:
            configure.append(f"-DREFII_EXTRACTOR_ZLIB_ROOT={args.zlib_source.resolve()}")
        run(configure, output / "configure.log", commands)
        if args.configure_only:
            return 0
        # Fable 3's bridge (librefiii_hostbridge.so, cmake/refii_android.cmake) and its hooks, built
        # here only so a hook that does not compile for ARM64 fails now rather than on the phone.
        targets = ["refii_hostbridge", "refiii_hostbridge", "refiii_hooks", "refii_lua",
                   "refii_binkdecode", "refii_xmadecode"]
        if args.selftests:
            targets += ["refii_android_platform_selftest", "refii_android_fiber_selftest",
                        "refii_android_sdl_probe"]
        if generated:
            targets.append("refii_android_game_module")
        # Corrosion links Rust with the NDK's bare clang and hands the Android target only to the
        # final crate; dependencies that link their own shared object (if-watch) then cannot find
        # Android's system libraries. Cargo's per-target flags reach every crate it builds.
        rust_env = {"CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS":
            f"-Clink-arg=--target=aarch64-linux-android{args.api}"} if args.rexnet else None
        run([args.cmake, "--build", str(build), "--target", *targets,
             "--parallel", str(args.jobs)], output / "build.log", commands, extra_env=rust_env)
        libraries = stage_closure(build, output / "native", ndk_bin)
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
        dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=source, text=True).strip())
        headers = [source / "src/runtime/include/rex/thread/mutex.h",
                   source / "tools/android/payload_abi_guard.h",
                   source / "rexglue-net/shadow/rexglue-sdk/include/rex/thread/fiber.h",
                   source / "rexglue-net/shadow/rexglue-sdk/include/rex/platform/fpscr.h",
                   source / "src/runtime/include/core/rex_payload_bridge.h",
                   sdk / "include/rex/ppc/context.h"]
        identity = {str(path.relative_to(source)) if source in path.parents else str(path): sha256(path)
                    for path in headers if path.is_file()}
        manifest = {"schema_version": 1, "abi": "arm64-v8a", "api": args.api,
            "ndk": (ndk / "source.properties").read_text(encoding="utf-8"),
            "source_commit": commit, "source_dirty": dirty,
            "compiler": str(executable(ndk_bin, "clang++")),
            "header_identity": identity, "payload_mutex_type": "payload_global_recursive_mutex",
            "libraries": libraries, "private_module_excluded": True,
            "seconds": time.monotonic() - started}
        (output / "native-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        if generated:
            module = build / "private-module/librefii_generated.so"
            object_audit = verify_payload_objects(build, ndk_bin)
            (output / "private-module-manifest.json").write_text(json.dumps({
                "private_game_derived": True, "path": str(module), "sha256": sha256(module),
                "api": args.api, "abi": "arm64-v8a", "header_identity": identity,
                "generated_directory": str(generated), "object_audit": object_audit,
                "generated_preparation": json.loads(
                    (build / "android-generated-manifest.json").read_text(encoding="utf-8")),
                **elf_info(executable(ndk_bin, "llvm-readelf"), module)
            }, indent=2), encoding="utf-8")
        print(f"Built {len(libraries)} redistributable native libraries: {output / 'native-manifest.json'}")
        return 0
    finally:
        (output / "commands.json").write_text(json.dumps(commands, indent=2), encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
