#!/usr/bin/env python3
"""Audit an ARM64 APK, native input provenance and optional PRIVATE module.

The module is inspected in place and never inserted into the APK. Reports and
extracted inspection copies belong outside the repository. No device is used.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import struct
import subprocess
import zipfile

from android_common import (data_sha256, dynamic_symbols, executable, inspect_elf,
                            ndk_prebuilt, private_resource, safe_relative, sha256)


def parse_badging(info: str) -> tuple[str, int, int]:
    """Accept current and older aapt2 labels, without guessing API values."""
    package = re.search(r"^package: name='([^']+)'", info, re.M)
    minimum = re.search(r"^(?:sdkVersion|minSdkVersion):'(\d+)'", info, re.M)
    target = re.search(r"^targetSdkVersion:'(\d+)'", info, re.M)
    if not package or not minimum or not target:
        raise ValueError("Cannot read APK package/minSDK/targetSDK")
    return package[1], int(minimum[1]), int(target[1])


NETPLAY_PERMISSIONS = {"android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE",
    "android.permission.ACCESS_WIFI_STATE", "android.permission.CHANGE_WIFI_MULTICAST_STATE"}

# Every build: the game's rumble vibrates the phone when played by touch (Input.vibrate_handheld).
GAME_PERMISSIONS = {"android.permission.VIBRATE"}


def permission_policy(info: str, debug_network: bool = False, phone_installer: bool = False,
        netplay: bool = False) -> dict:
    """Compare actual aapt2 manifest permissions with the explicit export policy.

    Every build requests VIBRATE, a normal permission, for the phone's rumble. A
    diagnostic build adds only INTERNET; this normal permission is needed even for TCP
    loopback. A phone installer additionally needs foreground local file processing
    and a bounded partial wake lock. A netplay build adds the network, Wi-Fi state and
    multicast permissions RexNet co-op needs; unrelated permissions remain rejected.
    """
    actual, implied = [], []
    for line in info.splitlines():
        if not line.startswith(("uses-permission", "uses-implied-permission")):
            continue
        match = re.fullmatch(r"(uses-permission(?:-sdk-\d+)?|uses-implied-permission): name='([^']+)'(?: .*)?", line)
        if not match:
            raise ValueError("Malformed APK permission declaration: " + line)
        target = implied if match[1] == "uses-implied-permission" else actual
        if match[2] in target:
            raise ValueError("Duplicate APK permission declaration: " + match[2])
        target.append(match[2])
    expected = set(GAME_PERMISSIONS)
    if debug_network:
        expected.add("android.permission.INTERNET")
    if netplay:
        expected |= NETPLAY_PERMISSIONS
    if phone_installer:
        expected |= {"android.permission.FOREGROUND_SERVICE", "android.permission.FOREGROUND_SERVICE_DATA_SYNC",
                     "android.permission.FOREGROUND_SERVICE_SPECIAL_USE", "android.permission.WAKE_LOCK"}
    requested = set(actual) | set(implied)
    missing, unexpected = sorted(expected - set(actual)), sorted(requested - expected)
    return dict(passed=not missing and not unexpected, debug_network=debug_network, netplay=netplay, phone_installer=phone_installer,
        declared=sorted(actual), implied=sorted(implied), expected=sorted(expected),
        missing=missing, unexpected=unexpected,
        scope="Actual signed APK aapt2 manifest permissions; exact default/diagnostic allowlist")


MANAGED_ENTRY = "assets/.godot/mono/publish/arm64/refii-launcher.dll"
NATIVEAOT_ENTRY = "lib/arm64-v8a/librefii-launcher.so"
NATIVEAOT_ENTRY_SYMBOL = "godotsharp_game_main_init"
MANAGED_RUNTIME_LIBRARIES = ("libmonosgen-2.0.so", "libcoreclr.so", "libhostfxr.so", "libhostpolicy.so")


def managed_layout(names: list[str], nativeaot: bool) -> list[str]:
    """Failures when the APK's managed runtime differs from the one the export selected.

    Mono embeds the IL assembly beside libmonosgen. NativeAOT (Godot's linux-bionic option)
    ships one native library that Godot loads only when no hostfxr, CoreCLR or Mono runtime
    is packaged, so any of those, or a leftover IL assembly, would run instead or mislead.
    """
    present = set(names)
    if not nativeaot:
        return [] if MANAGED_ENTRY in present else ["Missing embedded ARM64 managed assembly"]
    failures = []
    if NATIVEAOT_ENTRY not in present:
        failures.append("Missing NativeAOT game library " + NATIVEAOT_ENTRY)
    runtimes = sorted(name for name in present if name.startswith("lib/") and Path(name).name in MANAGED_RUNTIME_LIBRARIES)
    if runtimes:
        failures.append("NativeAOT APK also packages a managed runtime: " + ", ".join(runtimes))
    assemblies = sorted(name for name in present if name.startswith("assets/.godot/mono/publish/") and name.endswith(".dll"))
    if assemblies:
        failures.append("NativeAOT APK also embeds IL assemblies: " + ", ".join(assemblies[:5]))
    return failures


def native_inputs(manifest_path: Path) -> tuple[dict, dict[str, Path]]:
    """Validate every manifest input before packaging or accepting provenance."""
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("abi") != "arm64-v8a" or manifest.get("private_module_excluded") is not True:
        raise ValueError("Manifest must describe ARM64 production libraries and exclude the private module")
    paths = {}
    for row in manifest["libraries"]:
        path = Path(row["path"])
        if not path.is_absolute():
            path = manifest_path.parent.joinpath(*safe_relative(row["path"]).parts)
        name = path.name
        if private_resource(name) or "selftest" in name or name in paths:
            raise ValueError(f"Private, diagnostic or duplicate native library in manifest: {name}")
        if row.get("soname") != name or sha256(path) != row["sha256"]:
            raise ValueError(f"Native manifest SHA/SONAME mismatch: {path}")
        actual = inspect_elf(path.read_bytes())
        if actual["soname"] != name or sorted(actual["needed"]) != sorted(row["needed"]):
            raise ValueError(f"Native manifest differs from actual ELF: {path}")
        paths[name] = path.resolve()
    for required in ("librefii_hostbridge.so", "librexruntime.so", "libSDL3.so", "libc++_shared.so"):
        if required not in paths:
            raise ValueError(f"Required production library absent from manifest: {required}")
    return manifest, paths


def header_identity(value: dict) -> dict[str, str]:
    """Normalize separators and the external raw SDK root without losing identity.

    The builder can read the same pinned SDK from a different absolute checkout.
    Shadow overlay paths remain distinct from raw SDK headers.
    """
    if not isinstance(value, dict) or not value:
        raise ValueError("Nonempty recorded header identities are required")
    result = {}
    for name, digest in value.items():
        normalized = name.replace("\\", "/")
        if "/rexglue-sdk/include/" in normalized and "/shadow/" not in normalized:
            normalized = "sdk/include/" + normalized.split("/rexglue-sdk/include/", 1)[1]
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
            raise ValueError("Invalid header SHA256: " + name)
        digest = digest.lower()
        if normalized in result and result[normalized] != digest:
            raise ValueError("Ambiguous normalized header identity: " + normalized)
        result[normalized] = digest
    return result


def module_identity(native: dict, module: Path | None, private_manifest: Path | None, minimum: int) -> dict:
    """Match the private module's recorded build contract, separately from ELF imports.

    Metadata from the matching local builder adds evidence of header consistency;
    it does not establish every C++ ABI invariant or replace device execution.
    """
    if private_manifest is None:
        return dict(checked=False, reason="Private module manifest not supplied; binary closure alone does not prove matching C++ header ABI")
    if module is None:
        raise ValueError("--private-module-manifest requires --private-module")
    private = json.loads(private_manifest.read_text(encoding="utf-8"))
    if private.get("private_game_derived") is not True or private.get("sha256") != sha256(module):
        raise ValueError("Private module manifest classification/SHA256 differs from supplied binary")
    if private.get("abi") != native.get("abi") or private.get("abi") != "arm64-v8a":
        raise ValueError("Private module ABI differs from native manifest")
    api = private.get("api")
    if not isinstance(api, int) or isinstance(api, bool) or api < 31 or api > minimum:
        raise ValueError("Private module recorded Android API exceeds export floor or is unsupported")
    expected = header_identity(native.get("header_identity"))
    actual = header_identity(private.get("header_identity"))
    for name, digest in expected.items():
        if actual.get(name) != digest:
            raise ValueError("Private module missing/mismatched runtime header identity: " + name)
    if "payload_mutex_type" in private and private["payload_mutex_type"] != native.get("payload_mutex_type"):
        raise ValueError("Private module recorded mutex bridge differs from native manifest")
    object_audit = private.get("object_audit")
    if object_audit is not None and (object_audit.get("no_raw_recursive_mutex_imports") is not True or
            object_audit.get("shared_critical_region_bridge_imported") is not True):
        raise ValueError("Private module recorded object ABI audit did not pass")
    return dict(checked=True, manifest_sha256=sha256(private_manifest), module_sha256=private["sha256"],
        abi=private["abi"], api=api, matching_runtime_headers=len(expected),
        additional_module_headers=len(set(actual) - set(expected)), object_audit_recorded=object_audit is not None,
        scope="Module SHA and recorded header/ABI identities match; complete C++ ABI correctness still requires matching guarded builds and execution")


def audit(args: argparse.Namespace) -> dict:
    """Verify manifest, ZIP, ELF dependency/symbol closure and managed identity."""
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    manifest, inputs = native_inputs(args.native_manifest.resolve())
    identity = module_identity(manifest, args.private_module, getattr(args, "private_module_manifest", None), args.min_sdk)
    failures, libraries, paths = [], {}, {}
    build_tools = args.sdk / "build-tools" / args.build_tools
    aapt = executable(build_tools, "aapt2")
    info = subprocess.check_output([str(aapt), "dump", "badging", str(args.apk)], text=True)
    (out / "manifest-badging.txt").write_text(info, encoding="utf-8")
    package, minimum, target = parse_badging(info)
    if minimum != args.min_sdk or target != args.target_sdk or package != args.package:
        failures.append("Actual APK manifest differs from requested package/API levels")
    try:
        permissions = permission_policy(info, getattr(args, "debug_network", False), getattr(args, "phone_installer", False),
            getattr(args, "netplay", False))
        if not permissions["passed"]:
            failures.append("Actual APK permissions differ from explicit export policy: missing=" +
                repr(permissions["missing"]) + "; unexpected=" + repr(permissions["unexpected"]))
    except ValueError as error:
        permissions = dict(passed=False, error=str(error))
        failures.append(str(error))
    if minimum < int(manifest["api"]):
        failures.append("Manifest minimum API is below the native build API")
    nativeaot = getattr(args, "nativeaot", False)
    published_native = getattr(args, "published_native", None)
    shader_hashes = {}
    with zipfile.ZipFile(args.apk) as apk:
        names = apk.namelist()
        if len(names) != len(set(names)):
            failures.append("Duplicate APK ZIP entries")
        for entry in apk.infolist():
            name = entry.filename
            safe_relative(name)
            if private_resource(name):
                failures.append("Forbidden private/build resource: " + name)
            if name.endswith((".gdshader", ".gdshaderinc", ".glsl")):
                shader_hashes[name] = data_sha256(apk.read(entry))
            if not name.startswith("lib/") or not name.endswith(".so"):
                continue
            if not name.startswith("lib/arm64-v8a/"):
                failures.append("Unexpected native ABI: " + name)
                continue
            data = apk.read(entry)
            basename = Path(name).name
            path = out / "apk-native" / basename
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(data)
            row = inspect_elf(data)
            row.update(path=str(path), sha256=data_sha256(data), bytes=len(data), packaged=True)
            with args.apk.open("rb") as stream:
                stream.seek(entry.header_offset)
                header = stream.read(30)
            name_size, extra_size = struct.unpack_from("<HH", header, 26)
            offset = entry.header_offset + 30 + name_size + extra_size
            row["zip_data_offset"] = offset
            row["zip_stored_16k_aligned"] = entry.compress_type == zipfile.ZIP_STORED and offset % 16384 == 0
            if not row["zip_stored_16k_aligned"]:
                failures.append(basename + ": native ZIP entry must be stored and 16-KiB aligned")
            if basename in libraries:
                failures.append("Duplicate native basename: " + basename)
            libraries[basename], paths[basename] = row, path
        failures.extend(managed_layout(names, nativeaot))
        managed_sha = None
        if nativeaot:
            if NATIVEAOT_ENTRY in names:
                managed_sha = data_sha256(apk.read(NATIVEAOT_ENTRY))
                if published_native and managed_sha != sha256(published_native):
                    failures.append("Packaged NativeAOT library differs from the freshly published library")
        elif MANAGED_ENTRY in names:
            managed_sha = data_sha256(apk.read(MANAGED_ENTRY))
            config = json.loads(apk.read(MANAGED_ENTRY.replace(".dll", ".runtimeconfig.json")))
            if config.get("runtimeOptions", {}).get("tfm") != "net9.0":
                failures.append("Embedded managed target must be net9.0")
            if args.published_dll and managed_sha != sha256(args.published_dll):
                failures.append("Embedded assembly differs from freshly published assembly")
    for name, path in inputs.items():
        if name not in libraries or libraries[name]["sha256"] != sha256(path):
            failures.append("Packaged native input missing or differs: " + name)
        else:
            libraries[name]["provenance"] = str(path)
    if args.private_module:
        path = args.private_module.resolve()
        row = inspect_elf(path.read_bytes())
        row.update(path=str(path), sha256=sha256(path), bytes=path.stat().st_size, packaged=False,
                   private_game_derived=True)
        libraries[path.name], paths[path.name] = row, path
    prebuilt = ndk_prebuilt(args.ndk)
    readelf = executable(prebuilt / "bin", "llvm-readelf")
    system_root = prebuilt / "sysroot/usr/lib/aarch64-linux-android" / str(minimum)
    system = {p.name: p for p in system_root.glob("*.so") if p.name != "libc++.so"}
    if not system:
        raise ValueError(f"Missing NDK API {minimum} ARM64 stubs: {system_root}")
    for name, row in libraries.items():
        for valid, message in ((row["machine"] == 183 and row["type"] == 3, "requires ARM64 ET_DYN"),
            (row["soname_count"] == 1 and row["soname"] == name, "basename must equal one SONAME"),
            (row["load_alignment_16k"], "LOAD segments not 16-KiB compatible"),
            (row["relro_padding_safe_16k"], "rounded RELRO overlaps writable bytes"),
            (not row["text_relocations"], "contains text relocations"),
            (not row["rpath"], "contains RPATH/RUNPATH"),
            (all(api <= minimum for api in row["android_api_notes"]), "ELF Android API exceeds APK floor")):
            if not valid:
                failures.append(name + ": " + message)
    definitions = {}
    symbol_imports = {}
    for name, path in {**system, **paths}.items():
        definitions[name], symbol_imports[name] = dynamic_symbols(readelf, path)
    edges, unresolved, weak = [], [], []
    for name, row in libraries.items():
        for need in row["needed"]:
            provider = libraries.get(need)
            if provider:
                edges.append(dict(consumer=name, needed=need, resolution="bundled-exact-soname", sha256=provider["sha256"]))
            elif need in system:
                edges.append(dict(consumer=name, needed=need, resolution="public-NDK-API" + str(minimum)))
            else:
                failures.append(name + ": unresolved DT_NEEDED " + need)
        # Availability is checked against the complete app closure and public
        # API stubs, retaining exact symbol versions; execution tests still prove
        # actual Android loader visibility and ABI semantics.
        available = set().union(*definitions.values())
        for symbol, binding in symbol_imports[name]:
            if symbol not in available:
                row_missing = dict(consumer=name, symbol=symbol, binding=binding)
                (weak if binding == "WEAK" else unresolved).append(row_missing)
    if unresolved:
        failures.append(f"{len(unresolved)} strong dynamic symbols unavailable at API {minimum}")
    aot_library = Path(NATIVEAOT_ENTRY).name
    if nativeaot and aot_library in definitions and NATIVEAOT_ENTRY_SYMBOL not in definitions[aot_library]:
        failures.append(f"{aot_library} does not export Godot's NativeAOT entry {NATIVEAOT_ENTRY_SYMBOL}")
    if args.project:
        source_shaders = {"assets/" + p.relative_to(args.project).as_posix(): sha256(p)
            for p in args.project.rglob("*") if p.is_file() and p.suffix in (".gdshader", ".gdshaderinc", ".glsl")
            and not any(part in ("android", ".godot", "tests", "bin", "obj", "build") for part in p.relative_to(args.project).parts)}
        for name, digest in shader_hashes.items():
            if name in source_shaders and source_shaders[name] != digest:
                failures.append("Packaged authored shader changed: " + name)
        # Godot may compile/remap shaders and omit their text. Record such cases
        # explicitly rather than treating absence as byte-for-byte verification.
        shader_comparison = dict(packaged_sources=len(shader_hashes), source_files=len(source_shaders),
            exact_source_matches=sum(source_shaders.get(n) == h for n, h in shader_hashes.items()),
            source_text_not_packaged=sorted(set(source_shaders) - set(shader_hashes)))
    else:
        shader_comparison = None
    result = dict(passed=not failures, failures=failures, apk=str(args.apk.resolve()), apk_sha256=sha256(args.apk),
        package=package, min_sdk=minimum, target_sdk=target, abi="arm64-v8a",
        android_permissions=permissions,
        managed_runtime="nativeaot" if nativeaot else "mono", managed_sha256=managed_sha,
        published_dll_checked=bool(args.published_dll), published_native_checked=bool(published_native),
        private_module_inspected=bool(args.private_module), private_module_packaged=False,
        private_module_identity=identity,
        private_game_assets_or_saves_packaged=False, libraries=libraries, dependency_edges=edges,
        missing_strong_symbols=unresolved, missing_weak_symbols=weak,
        shader_comparison=shader_comparison, packaged_shader_hashes=shader_hashes,
        scope="Static APK/ELF provenance, API and alignment audit; device execution and 16-KiB guest semantics require separate tests")
    (out / "verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if failures:
        raise ValueError("APK validation failed: " + "; ".join(failures[:12]))
    return result


def arguments() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("apk", "native-manifest", "ndk", "sdk", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--private-module", type=Path)
    parser.add_argument("--private-module-manifest", type=Path, help="Optional matching private builder metadata; compares binary hash and runtime header identities")
    parser.add_argument("--published-dll", type=Path)
    parser.add_argument("--nativeaot", action="store_true",
        help="Require the NativeAOT layout: librefii-launcher.so with Godot's entry, and no Mono runtime or IL assemblies")
    parser.add_argument("--published-native", type=Path, help="With --nativeaot: the freshly published library to match")
    parser.add_argument("--project", type=Path)
    parser.add_argument("--build-tools", default="36.1.0")
    parser.add_argument("--min-sdk", type=int, default=33)
    parser.add_argument("--target-sdk", type=int, default=36)
    parser.add_argument("--package", default="org.refii.android")
    parser.add_argument("--debug-network", action="store_true",
        help="Require exactly Android INTERNET permission for an explicitly opted-in diagnostic APK")
    parser.add_argument("--phone-installer", action="store_true", help="Require the exact foreground installer permission set")
    parser.add_argument("--netplay", action="store_true", help="Require the exact RexNet co-op network permission set")
    return parser


if __name__ == "__main__":
    result = audit(arguments().parse_args())
    print(f"PASS: {len(result['libraries'])} ELF objects; manifest, provenance, strong symbols and alignment verified")
