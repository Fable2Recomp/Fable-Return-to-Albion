#!/usr/bin/env python3
"""Writes THIRD_PARTY_NOTICES.txt: the licence of every third-party component a release ships.

Run it again whenever a submodule, a rexnet crate, the Godot pin or the .NET SDK changes:

    python3 tools/licenses/gen_third_party_notices.py [--godot PATH] [--dotnet-root PATH]

Licence texts are copied from the files that ship with each component, never retyped. The few
texts that are not in the tree were fetched from upstream into tools/licenses/texts/. Identical texts are printed once,
under the list of components that use them.

Inputs it needs on the machine: cargo (for the rexnet dependency graph, read --offline), the
pinned Godot editor (it prints its own licence data), and the .NET SDK the export uses.
"""

import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TEXTS = ROOT / "tools/licenses/texts"
RX = "rexglue-net/rexglue-sdk/thirdparty"
OUT = ROOT / "THIRD_PARTY_NOTICES.txt"
SCRATCH = Path.home() / ".cache/refii-scratch/notices"

# The platforms a release is built for; crates needed only elsewhere (UEFI, wasm, macOS) are
# not in any shipped binary.
RELEASE_TARGETS = ["x86_64-unknown-linux-gnu", "aarch64-unknown-linux-gnu",
                   "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc"]

# A file reference is a path, or (path, first_line, last_line) for a licence carried in a
# source file's header comment.
NATIVE = [
    # --- rexglue: runtime, kernel and the code generator -------------------------------------
    dict(name="rexglue-sdk (includes code from Xenia)", url="https://github.com/Loreaxe/rexglue-sdk",
         spdx="BSD-3-Clause", files=["rexglue-net/rexglue-sdk/LICENSE"]),
    dict(name="GNU binutils PowerPC disassembler (in bin/recompile/bin/rexglue only)",
         url="https://www.gnu.org/software/binutils/", spdx="GPL-2.0-or-later",
         files=[(f"{RX}/disasm/ppc-dis.c", 1, 20), f"{RX}/FFmpeg/COPYING.GPLv2"]),
    dict(name="FFmpeg (libavcodec, libavutil)", url="https://github.com/wmarti/FFmpeg",
         spdx="LGPL-2.1-or-later", files=[f"{RX}/FFmpeg/LICENSE.md", f"{RX}/FFmpeg/COPYING.LGPLv2.1"]),
    dict(name="libmspack", url="https://github.com/kyz/libmspack", spdx="LGPL-2.1-only",
         files=[f"{RX}/libmspack/libmspack/COPYING.LIB"]),
    dict(name="{fmt}", url="https://github.com/fmtlib/fmt", spdx="MIT", files=[f"{RX}/fmt/LICENSE"]),
    dict(name="spdlog", url="https://github.com/gabime/spdlog", spdx="MIT", files=[f"{RX}/spdlog/LICENSE"]),
    dict(name="Dear ImGui", url="https://github.com/ocornut/imgui", spdx="MIT", files=[f"{RX}/imgui/LICENSE.txt"]),
    dict(name="toml++", url="https://github.com/marzer/tomlplusplus", spdx="MIT", files=[f"{RX}/tomlplusplus/LICENSE"]),
    dict(name="inja", url="https://github.com/pantor/inja", spdx="MIT", files=[f"{RX}/inja/LICENSE"]),
    dict(name="JSON for Modern C++ (via inja)", url="https://github.com/nlohmann/json", spdx="MIT",
         files=["tools/licenses/texts/nlohmann-json-LICENSE.MIT"]),
    dict(name="CLI11", url="https://github.com/CLIUtils/CLI11", spdx="BSD-3-Clause", files=[f"{RX}/cli11/LICENSE"]),
    dict(name="UTF8-CPP", url="https://github.com/nemtrif/utfcpp", spdx="BSL-1.0", files=[f"{RX}/utfcpp/LICENSE"]),
    dict(name="o1heap", url="https://github.com/pavel-kirienko/o1heap", spdx="MIT", files=[f"{RX}/o1heap/LICENSE"]),
    dict(name="SIMDe", url="https://github.com/simd-everywhere/simde", spdx="MIT", files=[f"{RX}/simde/COPYING"]),
    dict(name="xxHash", url="https://github.com/Cyan4973/xxHash", spdx="BSD-2-Clause", files=[f"{RX}/xxHash/LICENSE"]),
    dict(name="SDL 3", url="https://github.com/libsdl-org/SDL", spdx="Zlib", files=[f"{RX}/sdl3/LICENSE.txt"]),
    dict(name="Tracy Profiler", url="https://github.com/wolfpld/tracy", spdx="BSD-3-Clause", files=[f"{RX}/tracy/LICENSE"]),
    dict(name="disruptorplus", url="https://github.com/lewissbaker/disruptorplus", spdx="MIT",
         files=["tools/licenses/texts/disruptorplus-LICENSE.txt"]),
    dict(name="tiny-AES-c", url="https://github.com/kokke/tiny-AES-c", spdx="Unlicense",
         files=["tools/licenses/texts/tiny-AES-c-unlicense.txt"]),
    dict(name="aes_128", url="https://github.com/openluopworld/aes_128", spdx="MIT", files=[f"{RX}/aes_128/LICENSE"]),
    dict(name="DES implementation", spdx="see text", files=[f"{RX}/crypto/des/LICENSE"]),
    dict(name="RC4 implementation (Whistle Communications)", spdx="see text", files=[(f"{RX}/crypto/rc4.c", 1, 35)]),
    dict(name="Rijndael reference implementation", spdx="public domain",
         files=[(f"{RX}/crypto/rijndael-alg-fst.c", 1, 25)]),
    dict(name="SHA-256 (Stephan Brumme)", spdx="Zlib", files=[(f"{RX}/crypto/sha256.cpp", 1, 6)]),
    dict(name="TinySHA1", url="https://github.com/mohaps/TinySHA1", spdx="ISC",
         files=[(f"{RX}/crypto/TinySHA1.hpp", 1, 24)]),
    dict(name="stb_image", url="https://github.com/nothings/stb", spdx="MIT OR Unlicense",
         files=[(f"{RX}/stb/stb_image.h", 7946, 7984)]),
    dict(name="RenderDoc in-application API header", url="https://github.com/baldurk/renderdoc", spdx="MIT",
         files=[(f"{RX}/renderdoc/renderdoc_app.h", 1, 23)]),
    dict(name="DXBC checksum (AMD, derived from RSA MD5)", spdx="see text",
         files=[(f"{RX}/dxbc/DXBCChecksum.cpp", 1, 72)]),
    dict(name="Vulkan-Headers", url="https://github.com/KhronosGroup/Vulkan-Headers", spdx="Apache-2.0 OR MIT",
         files=[f"{RX}/vulkan-headers/LICENSE.md"]),
    dict(name="Vulkan-Loader", url="https://github.com/KhronosGroup/Vulkan-Loader", spdx="Apache-2.0",
         files=[f"{RX}/vulkan-loader/LICENSE.txt"]),
    dict(name="Vulkan Memory Allocator", url="https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator",
         spdx="MIT", files=[f"{RX}/vulkan-memory-allocator/LICENSE.txt"]),
    dict(name="SPIRV-Headers", url="https://github.com/KhronosGroup/SPIRV-Headers", spdx="MIT",
         files=[f"{RX}/spirv-headers/LICENSE"]),
    dict(name="SPIRV-Tools", url="https://github.com/KhronosGroup/SPIRV-Tools", spdx="Apache-2.0",
         files=[f"{RX}/spirv-tools/LICENSE"]),
    dict(name="glslang", url="https://github.com/KhronosGroup/glslang", spdx="BSD-3-Clause AND others, see text",
         files=[f"{RX}/glslang/LICENSE.txt"]),
    # --- refii ---------------------------------------------------------------------------------
    dict(name="Lua 5.1", url="https://www.lua.org", spdx="MIT", files=["src/lua/thirdparty/lua512/COPYRIGHT"]),
    dict(name="zlib", url="https://github.com/madler/zlib", spdx="Zlib", files=["src/extractor/thirdparty/zlib/LICENSE"]),
    dict(name="XenosRecomp", url="https://github.com/hedge-dev/XenosRecomp", spdx="MIT",
         files=["src/shader/thirdparty/XenosRecomp/LICENSE.md"]),
    dict(name="{fmt} (via XenosRecomp)", url="https://github.com/fmtlib/fmt", spdx="MIT",
         files=["src/shader/thirdparty/XenosRecomp/thirdparty/fmt/LICENSE"]),
    dict(name="SMOL-V (via XenosRecomp)", url="https://github.com/aras-p/smol-v", spdx="MIT",
         files=["src/shader/thirdparty/XenosRecomp/thirdparty/smol-v/license.txt"]),
    dict(name="xxHash (via XenosRecomp)", url="https://github.com/Cyan4973/xxHash", spdx="BSD-2-Clause",
         files=["src/shader/thirdparty/XenosRecomp/thirdparty/xxHash/LICENSE"]),
    dict(name="Zstandard (via XenosRecomp)", url="https://github.com/facebook/zstd", spdx="BSD-3-Clause",
         files=["src/shader/thirdparty/XenosRecomp/thirdparty/zstd/LICENSE"]),
    dict(name="extract-xiso (logic in Godot/src/xiso)", url="https://github.com/XboxDev/extract-xiso",
         spdx="BSD-4-Clause-style, see text", files=["Godot/src/xiso/extract-xiso-LICENSE.txt"]),
    dict(name="ncnn (texture upscaler, when built)", url="https://github.com/Tencent/ncnn", spdx="BSD-3-Clause",
         files=["src/thirdparty/ncnn/LICENSE.txt"]),
    # --- the bundled C++ toolchain ---------------------------------------------------------------
    dict(name="GNU libstdc++ headers (bin/recompile/stdcxx)", url="https://gcc.gnu.org/onlinedocs/libstdc++/",
         spdx="GPL-3.0-or-later WITH GCC-exception-3.1",
         files=["tools/licenses/texts/GCC-exception-3.1.txt", f"{RX}/FFmpeg/COPYING.GPLv3"]),
]

PREAMBLE = """\
THIRD-PARTY NOTICES
===================

Fable 2: Return to Albion is licensed under the MIT licence in LICENSE. A release also contains
the third-party software listed below, each under its own licence. The full text of every licence
follows its list of components; components that share an identical text are listed together.

Generated by tools/licenses/gen_third_party_notices.py. Do not edit by hand.


TERMS THAT GO BEYOND KEEPING A NOTICE
-------------------------------------

* The rexglue code generator, bin/recompile/bin/rexglue, contains the GNU binutils PowerPC
  disassembler and is therefore distributed under the GNU General Public License, version 2 or
  later. No other file in the release contains it. Its complete corresponding source is the
  Fable 2: Return to Albion source of the same version, including the rexglue-net/rexglue-sdk submodule
  (https://github.com/Loreaxe/rexglue-sdk) and the rexglue-net/shadow tree.

* FFmpeg (in librefii_binkdecode, librefii_xmadecode and the rexglue runtime) and libmspack (in
  the rexglue runtime) are licensed under the GNU Lesser General Public License, version 2.1.
  Their source is at the URLs listed below and in the Fable 2: Return to Albion source of the same version. You may
  rebuild those libraries from modified source and relink them.

* The GNU libstdc++ headers in bin/recompile/stdcxx are licensed under the GPL, version 3, with
  the GCC Runtime Library Exception, version 3.1. They are used to compile the game code on your
  machine; the exception lets the result be distributed under any terms.

* The Zig toolchain in bin/recompile/zig ships with its own LICENSE file and the licence files of
  the libraries it bundles, in that directory.

* On Windows the release includes the Microsoft Visual C++ runtime, redistributed under the
  Microsoft Visual Studio redistributable terms.

Fable 2: Return to Albion contains no code or data from Fable 2. The game code built on your machine and every game
asset belong to their owners.


CONTENTS
--------

  1. Native libraries and tools
  2. Rust crates (rexnet networking)
  3. Godot Engine
  4. .NET runtime
"""


def read_ref(ref):
    if isinstance(ref, tuple):
        path, first, last = ref
        lines = (ROOT / path).read_text(encoding="utf-8", errors="replace").splitlines()
        return "\n".join(lines[first - 1:last])
    return (ROOT / ref).read_text(encoding="utf-8", errors="replace")


def norm(text):
    return re.sub(r"\s+", " ", text).strip()


def grouped(entries):
    """entries: list of (label, text). Returns [(labels, text)] with identical texts merged."""
    order, by_key = [], {}
    for label, text in entries:
        key = norm(text)
        if key not in by_key:
            by_key[key] = ([], text.strip("\n"))
            order.append(key)
        by_key[key][0].append(label)
    return [by_key[k] for k in order]


def section(title, entries):
    out = [f"\n\n{'=' * 100}\n{title}\n{'=' * 100}\n"]
    for labels, text in grouped(entries):
        out.append("\n" + "-" * 100 + "\n")
        out.extend(f"{label}\n" for label in labels)
        out.append("\n" + text + "\n")
    return "".join(out)


def native_entries():
    entries = []
    for c in NATIVE:
        url = f" <{c['url']}>" if c.get("url") else ""
        label = f"{c['name']}{url} -- {c['spdx']}"
        entries.append((label, "\n\n".join(read_ref(r).strip("\n") for r in c["files"])))
    return entries


def cargo_entries():
    manifest = ROOT / "rexglue-net/rexglue-sdk/src/rexnet/core/Cargo.toml"
    cargo = os.environ.get("CARGO", "cargo")
    platforms = []
    for triple in RELEASE_TARGETS:
        platforms += ["--filter-platform", triple]
    meta = json.loads(subprocess.run(
        [cargo, "metadata", "--format-version", "1", "--offline", "--manifest-path", str(manifest), *platforms],
        check=True, capture_output=True, text=True).stdout)
    packages = {p["id"]: p for p in meta["packages"]}
    nodes = {n["id"]: n for n in meta["resolve"]["nodes"]}
    root = meta["resolve"]["root"]

    def proc_macro(p):
        return any("proc-macro" in t["kind"] for t in p["targets"])

    # Normal dependencies only, and nothing behind a proc-macro: those run in the compiler and
    # are not part of the shipped library.
    seen, stack = set(), [root]
    while stack:
        pid = stack.pop()
        if pid in seen:
            continue
        seen.add(pid)
        for dep in nodes[pid]["deps"]:
            if any(k["kind"] is None for k in dep["dep_kinds"]) and not proc_macro(packages[dep["pkg"]]):
                stack.append(dep["pkg"])
    seen.discard(root)

    entries, no_text, missing = [], [], []
    for pid in sorted(seen, key=lambda i: (packages[i]["name"], packages[i]["version"])):
        p = packages[pid]
        spdx = p.get("license") or "see text"
        folder = Path(p["manifest_path"]).parent
        files = sorted(f for f in folder.iterdir()
                       if f.is_file() and re.match(r"(?i)(licen[cs]e|copying|unlicense|notice|copyright)", f.name))
        # Where the crate offers MIT as one choice of an OR, ship under MIT.
        if " OR " in spdx.upper() or "/" in spdx:
            mit = [f for f in files if re.search(r"(?i)mit", f.name)]
            if mit and re.search(r"\bMIT\b", spdx):
                files = mit
        label = f"{p['name']} {p['version']} -- {spdx}"
        if p.get("repository"):
            label = f"{p['name']} {p['version']} <{p['repository']}> -- {spdx}"
        if files:
            entries.append((label, "\n\n".join(f.read_text(encoding="utf-8", errors="replace").strip("\n")
                                             for f in files)))
        elif re.search(r"\bMIT\b", spdx):
            authors = ", ".join(p.get("authors") or []) or "the crate's authors"
            no_text.append(f"{label}\n    Copyright (c) {authors}")
        else:
            missing.append(label)

    # These crates state MIT in Cargo.toml but publish no licence file, so the MIT text is printed
    # once for all of them with the authors each crate names.
    if no_text:
        entries.append(("\n".join(no_text) + "\n\n(These crates declare the MIT licence in their package "
                        "metadata without shipping its text; the standard text follows.)",
                        (TEXTS / "MIT.txt").read_text(encoding="utf-8")))
    return entries, missing


def godot_text(godot):
    SCRATCH.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=SCRATCH) as work:
        work = Path(work)
        (work / "project.godot").write_text("config_version=5\n")
        (work / "dump.gd").write_text("""extends SceneTree
func _init():
	var out := Engine.get_license_text() + "\\n"
	for c in Engine.get_copyright_info():
		out += "\\n* " + c["name"] + "\\n"
		for p in c["parts"]:
			out += "  Files: " + ", ".join(p["files"]) + "\\n"
			for cr in p["copyright"]:
				out += "  Copyright: " + cr + "\\n"
			out += "  License: " + p["license"] + "\\n"
	var info := Engine.get_license_info()
	var keys := info.keys()
	keys.sort()
	for k in keys:
		out += "\\n" + "-".repeat(100) + "\\nLicense: " + k + "\\n\\n" + info[k].strip_edges() + "\\n"
	var f := FileAccess.open("res://notices.txt", FileAccess.WRITE)
	f.store_string(out)
	f.close()
	quit()
""")
        env = dict(os.environ, TMPDIR=str(work), XDG_DATA_HOME=str(work / "xdg"),
                   XDG_CONFIG_HOME=str(work / "xdg"), XDG_CACHE_HOME=str(work / "xdg"))
        subprocess.run([str(godot), "--headless", "--path", str(work), "-s", "dump.gd"],
                       check=True, capture_output=True, env=env, timeout=300)
        version = subprocess.run([str(godot), "--version"], capture_output=True, text=True, env=env).stdout.strip()
        return version, (work / "notices.txt").read_text(encoding="utf-8")


def find_godot():
    found = sorted(glob.glob(str(ROOT / "tools/godot/Godot_v*_mono_*")), key=os.path.getmtime)
    found = [f for f in found if os.access(f, os.X_OK) and os.path.isfile(f)]
    return Path(found[-1]) if found else None


def find_dotnet_root():
    for candidate in (os.environ.get("DOTNET_ROOT"), "/usr/lib/dotnet", "/usr/share/dotnet",
                      str(Path.home() / ".dotnet")):
        if candidate and (Path(candidate) / "ThirdPartyNotices.txt").is_file():
            return Path(candidate)
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--godot", type=Path, default=find_godot(), help="the pinned Godot .NET editor")
    ap.add_argument("--dotnet-root", type=Path, default=find_dotnet_root(), help="the .NET SDK root")
    args = ap.parse_args()
    if not args.godot:
        sys.exit("No Godot editor under tools/godot; pass --godot.")
    if not args.dotnet_root:
        sys.exit("No .NET SDK with ThirdPartyNotices.txt found; pass --dotnet-root.")

    parts = [PREAMBLE]
    parts.append(section("1. NATIVE LIBRARIES AND TOOLS", native_entries()))

    crates, missing = cargo_entries()
    parts.append(section("2. RUST CRATES (rexnet networking, linked into the rexglue runtime)", crates))

    version, godot = godot_text(args.godot)
    parts.append(f"\n\n{'=' * 100}\n3. GODOT ENGINE ({version}) <https://godotengine.org> -- MIT\n"
                 f"{'=' * 100}\n\nGodot Engine and the components it contains, as reported by the engine.\n\n{godot}")

    dn = args.dotnet_root
    parts.append(f"\n\n{'=' * 100}\n4. .NET RUNTIME <https://github.com/dotnet/runtime> -- MIT\n{'=' * 100}\n\n"
                 + (dn / "LICENSE.txt").read_text(encoding="utf-8").strip("\n") + "\n\n" + "-" * 100 + "\n\n"
                 + (dn / "ThirdPartyNotices.txt").read_text(encoding="utf-8").strip("\n") + "\n")

    OUT.write_text("".join(parts), encoding="utf-8")
    print(f"Wrote {OUT.relative_to(ROOT)}: {len(NATIVE)} native components, {len(crates)} crates, "
          f"Godot {version}, .NET from {dn}")
    for label in missing:
        print(f"WARNING: crate ships no licence text and is not MIT; add its text by hand: {label}",
              file=sys.stderr)


if __name__ == "__main__":
    main()
