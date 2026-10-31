"""Portable validation helpers for the Android prototype build tools.

These tools deliberately do not infer a developer's SDK, signing key, game
directory, device serial or generated module from a machine-specific path.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path, PurePosixPath
import re
import struct
import subprocess


def sha256(path: Path) -> str:
    """Hash even a large private module with bounded memory use."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def data_sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def executable(directory: Path, name: str) -> Path:
    return directory / (name + (".exe" if os.name == "nt" else ""))


def ndk_prebuilt(ndk: Path) -> Path:
    root = ndk / "toolchains/llvm/prebuilt"
    candidates = [path for path in root.iterdir() if (path / "bin").is_dir()]
    if len(candidates) != 1:
        raise ValueError(f"Expected one host toolchain in {root}, got {candidates}")
    return candidates[0]


def safe_relative(name: str) -> PurePosixPath:
    """Reject ZIP/manifest paths that could leave an output root."""
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    if path.is_absolute() or not path.parts or any(p in ("..", ".") for p in normalized.split("/")) or ":" in path.parts[0]:
        raise ValueError(f"Unsafe relative path: {name}")
    return path


def private_resource(name: str) -> bool:
    """Identify private game inputs and build caches that must never enter an APK.

    Authored fonts, icons, shaders and source-only resource descriptions remain
    exportable. This complements source staging and explicit export exclusions;
    it is not a license classifier for arbitrary user-created files.
    """
    value = name.replace("\\", "/").lower()
    path = PurePosixPath(value)
    parts = path.parts
    blocked_directories = ("assets/save", "assets/game", "assets/gui", "tests/", "android/", "build/", "debug/")
    # Strip the APK resource prefix, but retain .godot's managed/import output.
    resource = value[7:] if value.startswith("assets/") else value
    if any(candidate == item.rstrip("/") or candidate.startswith(item.rstrip("/") + "/")
           for candidate in (resource, value) for item in blocked_directories):
        return True
    if path.suffix in {".bnk", ".xex", ".xexp", ".bik", ".iso", ".xcp", ".gdb", ".tex", ".mdl", ".f2mod", ".refii", ".save", ".keystore", ".jks", ".apk", ".aab"}:
        return True
    if path.name in {"mainsave.bin", "herosave.bin", "librefii_generated.so", "android-runtime.json", "refii.toml", "local.properties"}:
        return True
    return value.endswith(".gdb.tu") or bool(re.search(r"(?:^|/)refii_recomp\..*\.(?:cpp|h)$", value))


def inspect_elf(data: bytes) -> dict:
    """Inspect ELF64 program headers, dynamic names and Android API notes.

    RELRO is rounded to the actual device page size. An unaligned RELRO end is
    valid when linker padding prevents other writable bytes from being protected.
    This is a packaging check, not proof that guest memory supports 16-KiB pages.
    """
    if data[:6] != b"\x7fELF\x02\x01":
        raise ValueError("Expected little-endian ELF64")
    kind, machine, _, _, phoff, _, _, _, phsize, phcount, *_ = struct.unpack_from("<HHIQQQIHHHHHH", data, 16)
    if phsize != 56 or phoff + phsize * phcount > len(data):
        raise ValueError("Invalid ELF program header table")
    segments = []
    for i in range(phcount):
        typ, flags, off, va, _, fs, ms, align = struct.unpack_from("<IIQQQQQQ", data, phoff + i * phsize)
        if off + fs > len(data):
            raise ValueError("ELF segment exceeds file")
        segments.append(dict(type=typ, flags=flags, offset=off, virtual_address=va,
                             file_size=fs, memory_size=ms, alignment=align))
    loads = [s for s in segments if s["type"] == 1]
    dyn = next((s for s in segments if s["type"] == 2), None)
    tags = []
    if dyn:
        for off in range(dyn["offset"], dyn["offset"] + dyn["file_size"], 16):
            tag, value = struct.unpack_from("<qQ", data, off)
            if not tag:
                break
            tags.append((tag, value))
    address = next((value for tag, value in tags if tag == 5), None)
    string_offset = None
    if address is not None:
        for s in loads:
            if s["virtual_address"] <= address < s["virtual_address"] + s["file_size"]:
                string_offset = s["offset"] + address - s["virtual_address"]
                break
        if string_offset is None:
            raise ValueError("Dynamic string table outside LOAD segments")

    def string(value: int) -> str:
        if string_offset is None:
            raise ValueError("Missing dynamic string table")
        start = string_offset + value
        return data[start:data.index(b"\0", start)].decode("utf-8")

    sonames = [string(v) for t, v in tags if t == 14]
    relro = [s for s in segments if s["type"] == 0x6474E552]
    apis = []
    for s in segments:
        if s["type"] != 4:
            continue
        offset, limit = s["offset"], s["offset"] + s["file_size"]
        while offset + 12 <= limit:
            ns, ds, typ = struct.unpack_from("<III", data, offset)
            offset += 12
            name = data[offset:offset + ns].rstrip(b"\0")
            offset += (ns + 3) // 4 * 4
            desc = data[offset:offset + ds]
            offset += (ds + 3) // 4 * 4
            if name == b"Android" and typ == 1 and len(desc) >= 4:
                apis.append(struct.unpack_from("<I", desc)[0])
    padding_safe = True
    for r in relro:
        start, end = r["virtual_address"], r["virtual_address"] + r["memory_size"]
        ps, pe = start // 16384 * 16384, (end + 16383) // 16384 * 16384
        for s in loads:
            if s["flags"] & 2:
                a, b = max(ps, s["virtual_address"]), min(pe, s["virtual_address"] + s["memory_size"])
                if a < b and (a < start or b > end):
                    padding_safe = False
    return dict(machine=machine, type=kind, soname=sonames[0] if len(sonames) == 1 else None,
        soname_count=len(sonames), needed=[string(v) for t, v in tags if t == 1], android_api_notes=apis,
        load_segments=loads, load_alignment_16k=bool(loads) and all(s["alignment"] >= 16384 and
            s["alignment"] & (s["alignment"] - 1) == 0 and
            (s["virtual_address"] - s["offset"]) % 16384 == 0 for s in loads),
        relro_padding_safe_16k=padding_safe,
        text_relocations=any(t == 22 or t == 30 and v & 4 for t, v in tags),
        rpath=[string(v) for t, v in tags if t in (15, 29)])


def dynamic_symbols(readelf: Path, path: Path) -> tuple[set[str], list[tuple[str, str]]]:
    """Read dynamic definitions and imports, retaining symbol version identity."""
    output = subprocess.check_output([str(readelf), "--dyn-syms", "--wide", str(path)], text=True)
    exports, imports = set(), []
    for line in output.splitlines():
        match = re.match(r"\s*\d+:\s+\S+\s+\S+\s+\S+\s+(GLOBAL|WEAK)\s+\S+\s+(\S+)\s+(.+?)\s*$", line)
        if not match:
            continue
        bind, section, name = match.groups()
        name = name.split()[0]
        if section == "UND":
            imports.append((name.replace("@@", "@"), bind))
        else:
            exports.add(name.replace("@@", "@"))
            if "@@" in name:
                exports.add(name.split("@@")[0])
    return exports, imports


EXPORT_TEARDOWN_WHITELIST = 'ERROR: EditorSettings not instantiated yet when getting setting "export/android/shutdown_adb_on_exit".'


def export_errors(text: str) -> list[str]:
    """Godot can exit successfully after a failed managed publish/export."""
    return [line for line in text.splitlines() if line.strip() != EXPORT_TEARDOWN_WHITELIST and
            re.search(r"Build FAILED|\berror (?:CS|MSB|NETSDK)\d+|^ERROR:", line, re.I)]
