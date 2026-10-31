#!/usr/bin/env python3
"""Stage source, export a .NET Android APK, package native closure and sign it.

All generated files, tool preferences, caches, keys and logs stay outside the
checkout. This debug prototype contains no game module, game assets or saves.
No device is connected or modified by this command.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import time
import zipfile
import xml.etree.ElementTree as ET

from android_common import (EXPORT_TEARDOWN_WHITELIST, executable, export_errors, ndk_prebuilt,
                            private_resource, safe_relative, sha256)


def load_verifier():
    spec = importlib.util.spec_from_file_location("refii_verify_apk", Path(__file__).with_name("verify-apk.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_base_entry(target: zipfile.ZipFile, entry: zipfile.ZipInfo, data: bytes) -> None:
    """Keep native ELF entries stored for 16KiB alignment, including extraction-enabled builds.

    The phone compiler requires extractNativeLibs in the manifest. Godot also
    compresses native entries for that setting; retain the existing stored ELF
    contract when repackaging, independently of the manifest's extraction policy.
    """
    compression = zipfile.ZIP_STORED if entry.filename.startswith("lib/") and entry.filename.endswith(".so") else entry.compress_type
    target.writestr(entry, data, compress_type=compression)


def verify_phone_bundle_archive(archive: zipfile.ZipFile, expected: str) -> str:
    """Prove the signed APK contains the exact audited public compiler archive."""
    name = "assets/phonecompiler/data.zip"
    if archive.namelist().count(name) != 1:
        raise ValueError("Signed APK must contain exactly one public compiler archive")
    actual = hashlib.sha256(archive.read(name)).hexdigest()
    if actual != expected:
        raise ValueError("Signed APK compiler archive differs from the audited inputs")
    return actual


def stage_install_service(project: Path) -> None:
    """Install authored Java into a fresh Godot template and declare only bounded installer capabilities."""
    android = "{http://schemas.android.com/apk/res/android}"
    manifest = project / "android/build/src/main/AndroidManifest.xml"
    tree = ET.parse(manifest)
    root = tree.getroot()
    app = root.find("application")
    if app is None:
        raise ValueError("Android template has no application")
    for name in ("FOREGROUND_SERVICE", "FOREGROUND_SERVICE_DATA_SYNC", "WAKE_LOCK"):
        permission = "android.permission." + name
        if not any(row.get(android + "name") == permission for row in root.findall("uses-permission")):
            ET.SubElement(root, "uses-permission", {android + "name": permission})
    if any(row.get(android + "name") == "com.godot.game.refii.RefiiInstallService" for row in app.findall("service")):
        raise ValueError("Installer service already staged")
    ET.SubElement(app, "service", {android + "name": "com.godot.game.refii.RefiiInstallService",
        android + "exported": "false", android + "stopWithTask": "false", android + "foregroundServiceType": "dataSync"})
    ET.SubElement(app, "meta-data", {android + "name": "org.godotengine.plugin.v2.RefiiInstall",
        android + "value": "com.godot.game.refii.RefiiInstallPlugin"})
    ET.register_namespace("android", "http://schemas.android.com/apk/res/android")
    ET.register_namespace("tools", "http://schemas.android.com/tools")
    tree.write(manifest, encoding="utf-8", xml_declaration=True)
    destination = project / "android/build/src/main/java/com/godot/game/refii"
    destination.mkdir(parents=True, exist_ok=False)
    for name in ("RefiiInstallPlugin.java", "RefiiInstallService.java"):
        shutil.copyfile(project / "src/android/installer" / name, destination / name)


def external_directory(source: Path, path: Path, label: str) -> Path:
    """Reject both lexical and resolved paths within the checkout."""
    resolved = path.resolve()
    lexical = Path(os.path.abspath(path))
    if any(p == source or source in p.parents for p in (resolved, lexical)):
        raise ValueError(label + " must be outside the source checkout")
    return resolved


def source_junction(path: Path) -> bool:
    """Detect Windows junctions also on Python 3.10/3.11."""
    method = getattr(path, "is_junction", None)
    if callable(method):
        return method()
    try:
        return getattr(path.lstat(), "st_reparse_tag", 0) == 0xA0000003
    except FileNotFoundError:
        return False


def validate_source_path(source: Path, path: Path, root: Path | None = None) -> None:
    """Refuse links/junctions at every source ancestor before resolving paths."""
    root = root or source / "Godot"
    current = source
    for part in path.relative_to(source).parts:
        current = current / part
        if current.is_symlink() or source_junction(current):
            raise ValueError(f"Project source links/junctions require manual review: {current}")
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError(f"Project source resolved outside {root.name}: {path}")


def source_files(source: Path) -> list[Path]:
    """Git's source inventory includes local edits but excludes ignored secrets.

    Explicit exclusions are defense in depth: tracked private inputs and resource
    caches also must not enter the staged project, even if accidentally added.
    """
    names = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others",
        "--exclude-standard", "--", "Godot"], cwd=source).decode("utf-8").split("\0")
    result = []
    for name in sorted(set(filter(None, names))):
        relative = safe_relative(name)
        if relative.parts[0] != "Godot":
            raise ValueError("Git inventory escaped Godot project")
        parts = relative.parts[1:]
        if parts[0] in ("android", "debug", "levels") or any(p in (".godot", ".gradle", "bin", "obj", "tests", "build") for p in parts):
            continue
        path = source.joinpath(*relative.parts)
        if private_resource("/".join(parts)):
            continue
        validate_source_path(source, path)
        if path.is_file():
            result.append(path)
    if not any(p.name == "project.godot" for p in result):
        raise ValueError("No Godot project in Git source inventory")
    return result


NETPLAY_PERMISSIONS = ("internet", "access_network_state", "access_wifi_state", "change_wifi_multicast_state")


NATIVEAOT_OPTION = "dotnet/android_use_linux_bionic"
NATIVEAOT_RID = "linux-bionic-arm64"


def set_preset(text: str, package: str, minimum: int, target: int, debug_network: bool = False,
        netplay: bool = False, nativeaot: bool = False) -> str:
    """Change only the Android preset in the staged copy.

    Netplay adds exactly the permissions RexNet co-op needs: sockets, the network and Wi-Fi
    state its address discovery reads, and the multicast lock that lets LAN mDNS through.
    NativeAOT sets Godot's linux-bionic option, which publishes for linux-bionic-arm64 and
    packages one native game library in place of Mono and the IL assemblies.
    """
    sections = re.split(r"(?m)(?=^\[)", text)
    number = None
    for section in sections:
        match = re.match(r"\[preset\.(\d+)\]", section)
        if match and 'name="Android ARM64"' in section:
            number = match[1]
    if number is None:
        raise ValueError("Android ARM64 preset missing")
    for i, section in enumerate(sections):
        if section.startswith(f"[preset.{number}.options]"):
            for key, value in {"package/unique_name": json.dumps(package), "gradle_build/min_sdk": json.dumps(str(minimum)),
                "gradle_build/target_sdk": json.dumps(str(target))}.items():
                section, count = re.subn(r"(?m)^" + re.escape(key) + r"=.*$", key + "=" + value, section)
                if count != 1:
                    raise ValueError("Missing or duplicate Android option: " + key)
            internet = re.findall(r"(?m)^permissions/internet=(.*)$", section)
            if len(internet) > 1 or any(value.strip() not in ("true", "false") for value in internet):
                raise ValueError("Duplicate or malformed Android Internet permission option")
            if internet and internet[0].strip() == "true" and not (debug_network or netplay):
                raise ValueError("Android Internet permission requires explicit --debug-network or --netplay opt-in")
            for name in (NETPLAY_PERMISSIONS if netplay else ("internet",) if debug_network else ()):
                key = "permissions/" + name
                if re.search(r"(?m)^" + re.escape(key) + "=", section):
                    section = re.sub(r"(?m)^" + re.escape(key) + "=.*$", key + "=true", section)
                else:
                    section = section.rstrip() + "\n" + key + "=true\n"
            bionic = re.findall(r"(?m)^" + re.escape(NATIVEAOT_OPTION) + r"=(.*)$", section)
            if len(bionic) > 1 or any(value.strip() not in ("true", "false") for value in bionic):
                raise ValueError("Duplicate or malformed Android NativeAOT option")
            if bionic and bionic[0].strip() == "true" and not nativeaot:
                raise ValueError("Android NativeAOT option requires explicit --nativeaot opt-in")
            if nativeaot:
                if bionic:
                    section = re.sub(r"(?m)^" + re.escape(NATIVEAOT_OPTION) + "=.*$", NATIVEAOT_OPTION + "=true", section)
                else:
                    section = section.rstrip() + "\n" + NATIVEAOT_OPTION + "=true\n"
            sections[i] = section
    return "".join(sections)


def android_export_settings(text: str, rendering_method: str = "forward_plus") -> str:
    """Configure only the staged Android export's import/editor environment.

    This adds an export texture representation; authored shaders, HDR settings,
    runtime game texture conversion and desktop project settings stay intact.
    """
    if rendering_method not in ("forward_plus", "mobile"):
        raise ValueError("Unsupported Android rendering method")
    key = "textures/vram_compression/import_etc2_astc"
    if re.search(r"(?m)^" + re.escape(key) + "=", text):
        text = re.sub(r"(?m)^" + re.escape(key) + "=.*$", key + "=true", text)
    elif "[rendering]" not in text:
        text += "\n[rendering]\n" + key + "=true\n"
    else:
        text = text.replace("[rendering]", "[rendering]\n\n" + key + "=true", 1)
    # These are desktop authoring plugins, not Android runtime dependencies.
    # Their _EnterTree Scan call reenters the initial headless filesystem scan.
    sections = re.split(r"(?m)(?=^\[)", text)
    for i, section in enumerate(sections):
        if section.startswith("[editor_plugins]"):
            sections[i] = re.sub(r"(?m)^enabled=.*$", "enabled=PackedStringArray()", section)
        elif section.startswith("[rendering]"):
            # The Android override leaves the desktop rendering method intact.
            key = "renderer/rendering_method.android"
            setting = key + "=" + json.dumps(rendering_method)
            section, count = re.subn(r"(?m)^" + re.escape(key) + r"=.*$", setting, section)
            if count > 1:
                raise ValueError("Duplicate Android rendering method setting")
            sections[i] = section if count else section.replace("[rendering]", "[rendering]\n\n" + setting, 1)
    return "".join(sections)


def phone_compiler_settings(project_text: str, preset_text: str) -> tuple[str, str]:
    """Opt only the staged Android build into the self-contained compiler launcher.

    Tools execute from the APK's extracted read-only native library directory.
    The data ZIP is public headers/sysroot/own source, never generated game code.
    Desktop entry points and all other platform presets remain unchanged.
    """
    project_text, count = re.subn(r'(?m)^run/main_scene\.android=.*$',
        'run/main_scene.android="res://main.tscn"', project_text)
    if count != 1:
        raise ValueError("Require exactly one Android entry scene override")
    sections = re.split(r"(?m)(?=^\[)", preset_text)
    number = next((re.match(r"\[preset\.(\d+)\]", section).group(1) for section in sections
        if re.match(r"\[preset\.(\d+)\]", section) and 'name="Android ARM64"' in section), None)
    if number is None:
        raise ValueError("Android compiler preset missing")
    for i, section in enumerate(sections):
        if section.startswith(f"[preset.{number}]"):
            match = re.search(r'(?m)^include_filter="([^"]*)"$', section)
            if not match:
                raise ValueError("Missing Android resource include filter")
            filters = [value for value in match.group(1).split(',') if value]
            if 'phonecompiler/data.zip' not in filters:
                filters.append('phonecompiler/data.zip')
            sections[i] = re.sub(r'(?m)^include_filter=.*$',
                'include_filter=' + json.dumps(','.join(filters)), section)
        elif section.startswith(f"[preset.{number}.options]"):
            section, count = re.subn(r'(?m)^gradle_build/compress_native_libraries=.*$',
                'gradle_build/compress_native_libraries=true', section)
            if count != 1:
                raise ValueError("Missing Android native-library extraction setting")
            sections[i] = section
    return project_text, "".join(sections)


BUNDLED_MODS = "bundled-mods.zip"


def bundled_mod_files(source: Path) -> list[Path]:
    """Git's inventory of mods-src: the mods that ship with the app, as their source."""
    names = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others",
        "--exclude-standard", "--", "mods-src"], cwd=source).decode("utf-8").split("\0")
    result = []
    for name in sorted(set(filter(None, names))):
        relative = safe_relative(name)
        if relative.parts[0] != "mods-src" or len(relative.parts) < 3:
            raise ValueError("Bundled mod file outside a mods-src/<mod>/ folder: " + name)
        path = source.joinpath(*relative.parts)
        validate_source_path(source, path, source / "mods-src")
        if path.is_file():
            result.append(path)
    return result


def stage_bundled_mods(source: Path, project: Path) -> dict:
    """Zip mods-src into the staged project as res://bundled-mods.zip, with the release's manifest.txt.

    The desktop release ships the same trees as bundled-mods/ (cmake/refii_run_godot_export.cmake);
    the Android launcher unpacks the zip and packs each mod into assets/mods, switched off
    (Engine_ModBundled.cs). Source, not .f2mod packages: a package is the player's own and
    never enters an APK. Fixed timestamps keep the archive's bytes a function of the sources.
    """
    root = source / "mods-src"
    files = bundled_mod_files(source)
    listed = [p.relative_to(root).as_posix() for p in files]
    target = project / BUNDLED_MODS
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for path, name in zip(files, listed):
            archive.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)), path.read_bytes(), zipfile.ZIP_DEFLATED)
        archive.writestr(zipfile.ZipInfo("manifest.txt", (1980, 1, 1, 0, 0, 0)),
            "".join(name + "\n" for name in listed), zipfile.ZIP_DEFLATED)
    return dict(mods=sorted({name.split("/")[0] for name in listed}), files=len(listed), sha256=sha256(target))


def include_resource(preset_text: str, name: str) -> str:
    """Add one raw file to the Android preset's resource include filter (idempotent)."""
    sections = re.split(r"(?m)(?=^\[)", preset_text)
    number = next((re.match(r"\[preset\.(\d+)\]", section).group(1) for section in sections
        if re.match(r"\[preset\.(\d+)\]", section) and 'name="Android ARM64"' in section), None)
    if number is None:
        raise ValueError("Android ARM64 preset missing")
    for i, section in enumerate(sections):
        if section.startswith(f"[preset.{number}]"):
            match = re.search(r'(?m)^include_filter="([^"]*)"$', section)
            if not match:
                raise ValueError("Missing Android resource include filter")
            filters = [value for value in match.group(1).split(',') if value]
            if name not in filters:
                filters.append(name)
            sections[i] = re.sub(r'(?m)^include_filter=.*$',
                'include_filter=' + json.dumps(','.join(filters)), section)
    return "".join(sections)


def stage_phone_compiler(bundle: Path, project: Path, native_manifest: Path) -> dict:
    """Check complete public archive/native identity before staging any compiler resource."""
    proof = json.loads((bundle / "bundle-proof.json").read_text(encoding="utf-8"))
    data = bundle / "data.zip"
    if proof.get("schema") != 1 or proof.get("contains_game_files") is not False or \
        proof.get("private_module_excluded") is not True or sha256(data) != proof.get("data_sha256") or \
        sha256(native_manifest) != proof.get("native_manifest_sha256"):
        raise ValueError("Compiler bundle provenance/hash mismatch")
    with zipfile.ZipFile(data) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or "bundle.json" not in names:
            raise ValueError("Compiler archive has duplicate paths or no manifest")
        for entry in archive.infolist():
            safe_relative(entry.filename)
            if private_resource(entry.filename) or (entry.external_attr >> 16) & 0xf000 == 0xa000:
                raise ValueError("Private/link resource in compiler bundle: " + entry.filename)
        recipe = json.loads(archive.read("bundle.json"))
        if recipe.get("Schema") != 1 or recipe.get("Abi") != "arm64-v8a" or recipe.get("Api") != 31 or \
            recipe.get("Ndk") != "28.2.13676358" or recipe.get("SdkPin") != "bc812c08ab2efd4ed8d7f306bd1911c703771732":
            raise ValueError("Compiler bundle SDK/ABI pins mismatch")
        files = recipe.get("Files", {})
        if set(files) != set(names) - {"bundle.json"}:
            raise ValueError("Compiler data file inventory mismatch")
        from android_common import data_sha256
        for name, digest in files.items():
            if data_sha256(archive.read(name)) != digest:
                raise ValueError("Compiler data file hash mismatch: " + name)
        _, libraries = load_verifier().native_inputs(native_manifest)
        if recipe.get("NativeFiles") != {name: sha256(path) for name, path in libraries.items()}:
            raise ValueError("Compiler recipe does not match packaged native tools/runtime")
    target = project / "phonecompiler/data.zip"
    target.parent.mkdir(exist_ok=True)
    shutil.copyfile(data, target)
    return proof


def decode_launch_arguments(data: bytes) -> list[str]:
    """Read Godot's Android _cl_: little-endian uint32 count and UTF-8 lengths."""
    if len(data) < 4:
        raise ValueError("Truncated Android launch argument count")
    count, = struct.unpack_from("<I", data)
    if count > (len(data) - 4) // 4:
        raise ValueError("Truncated Android launch argument lengths")
    offset, result = 4, []
    for _ in range(count):
        if offset + 4 > len(data):
            raise ValueError("Truncated Android launch argument length")
        length, = struct.unpack_from("<I", data, offset)
        offset += 4
        if offset + length > len(data):
            raise ValueError("Truncated Android launch argument")
        value = data[offset:offset + length].decode("utf-8")
        if "\0" in value:
            raise ValueError("NUL in Android launch argument")
        result.append(value)
        offset += length
    if offset != len(data):
        raise ValueError("Unexpected trailing Android launch bytes")
    return result


def encode_launch_arguments(values: list[str]) -> bytes:
    """Encode byte lengths, rather than character counts, for Godot's launcher."""
    result = bytearray(struct.pack("<I", len(values)))
    for value in values:
        if "\0" in value:
            raise ValueError("NUL in Android launch argument")
        data = value.encode("utf-8")
        result.extend(struct.pack("<I", len(data)))
        result.extend(data)
    return bytes(result)


def configure_launch_arguments(data: bytes, rendering_method: str, gpu_profile: bool = False) -> bytes:
    """Make requested renderer/profiling explicit without ambiguous overrides.

    The Mobile command-line override was verified on the Android 12 test phone.
    Keep it alongside the staged project setting, and reject conflicting or
    ambiguous existing overrides instead of depending on argument precedence.
    GPU profiling is opt-in because timestamp collection can affect timings.
    Engine arguments must precede Godot's ``--`` user-argument separator.
    """
    if rendering_method not in ("forward_plus", "mobile"):
        raise ValueError("Unsupported Android rendering method")
    values = decode_launch_arguments(data)
    engine_end = values.index("--") if "--" in values else len(values)
    engine_values = values[:engine_end]
    profile_flags = [value for value in engine_values
                     if value == "--gpu-profile" or value.startswith("--gpu-profile=")]
    if len(profile_flags) > 1 or any(value != "--gpu-profile" for value in profile_flags):
        raise ValueError("Duplicate or malformed GPU profiling launch argument")
    if profile_flags and not gpu_profile:
        raise ValueError("GPU profiling launch argument requires explicit --gpu-profile opt-in")
    found = []
    for index, value in enumerate(engine_values):
        if value == "--rendering-method":
            if index + 1 == len(engine_values) or engine_values[index + 1].startswith("--"):
                raise ValueError("Missing rendering method launch value")
            found.append(engine_values[index + 1])
        elif value.startswith("--rendering-method="):
            found.append(value.split("=", 1)[1])
    if len(found) > 1 or (found and found[0] != rendering_method):
        raise ValueError("Conflicting or duplicate rendering method launch arguments")
    additions = []
    if not found and rendering_method != "forward_plus":
        additions.extend(["--rendering-method", rendering_method])
    if gpu_profile and not profile_flags:
        additions.append("--gpu-profile")
    if not additions:
        return data
    return encode_launch_arguments(values[:engine_end] + additions + values[engine_end:])


def install_template(templates: Path, project: Path, editor: Path) -> str:
    """Install matching official templates into isolated project/editor roots."""
    version = (templates / "version.txt").read_text(encoding="utf-8").strip()
    if version != "4.7.1.stable.mono":
        raise ValueError("Expected official Godot 4.7.1 .NET templates")
    template_root = editor.parent / "editor_data/export_templates" / version
    template_root.mkdir(parents=True)
    for name in ("android_debug.apk", "android_release.apk", "android_source.zip", "version.txt"):
        shutil.copyfile(templates / name, template_root / name)
    android = project / "android"
    android.mkdir()
    (android / ".gdignore").write_text("", encoding="utf-8")
    (android / ".build_version").write_text(version + "\n", encoding="utf-8")
    build = android / "build"
    build.mkdir()
    with zipfile.ZipFile(templates / "android_source.zip") as archive:
        for entry in archive.infolist():
            path = safe_relative(entry.filename)
            if not entry.is_dir():
                target = build.joinpath(*path.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(entry))
                if path.name == "gradlew" and os.name != "nt":
                    target.chmod(0o755)
    return version


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("output", "godot", "templates", "dotnet", "jdk", "sdk", "ndk", "native-manifest"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--cache-root", type=Path, help="Optional external cache root with gradle-home/ and nuget-packages/")
    parser.add_argument("--gradle-archive", type=Path, help="Optional official Gradle 8.11.1 ZIP to avoid wrapper download")
    parser.add_argument("--private-module", type=Path, help="Optional PRIVATE module, inspected only; never packaged")
    parser.add_argument("--private-module-manifest", type=Path, help="Optional matching private builder metadata for module/header identity validation")
    parser.add_argument("--min-sdk", type=int, default=33)
    parser.add_argument("--target-sdk", type=int, default=36)
    parser.add_argument("--package", default="org.refii.android")
    parser.add_argument("--build-tools", default="36.1.0")
    parser.add_argument("--rendering-method", choices=("forward_plus", "mobile"), default="forward_plus",
        help="Android renderer in the staged project and APK; default preserves Forward+ launch arguments")
    parser.add_argument("--gpu-profile", action="store_true",
        help="Opt in to Godot GPU timestamp profiling in the packaged launch arguments; may affect performance")
    parser.add_argument("--phone-compiler-bundle", type=Path,
        help="Verified public compiler bundle; selects the optional on-phone launcher and native extraction")
    parser.add_argument("--debug-network", action="store_true",
        help="Opt in to Android INTERNET manifest permission for the loopback debug channel; does not enable the channel")
    parser.add_argument("--netplay", action="store_true",
        help="Opt in to the network, Wi-Fi state and multicast permissions RexNet co-op needs")
    parser.add_argument("--nativeaot", action="store_true",
        help="Compile the C# game ahead of time with NativeAOT (Godot's linux-bionic option) instead of running it "
        "on Mono; the APK carries librefii-launcher.so and no Mono runtime or IL assemblies")
    parser.add_argument("--keystore", type=Path, help="Existing signing key; absent means create a new LOCAL debug key")
    parser.add_argument("--alias", default="androiddebugkey")
    parser.add_argument("--store-pass-env", default="REFII_ANDROID_STORE_PASSWORD")
    parser.add_argument("--key-pass-env", default="REFII_ANDROID_KEY_PASSWORD")
    args = parser.parse_args()
    source = Path(__file__).resolve().parents[2]
    try:
        output = external_directory(source, args.output, "Output")
        if args.cache_root:
            args.cache_root = external_directory(source, args.cache_root, "Cache root")
    except ValueError as error:
        parser.error(str(error))
    if output.exists():
        parser.error("Output already exists; choose a new path to preserve prior artifacts")
    if args.min_sdk < 31 or args.target_sdk < args.min_sdk:
        parser.error("Require minSDK>=31 and targetSDK>=minSDK")
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+", args.package):
        parser.error("Invalid Android package name")
    for name in ("godot", "templates", "dotnet", "jdk", "sdk", "ndk", "native_manifest", "cache_root", "gradle_archive", "keystore", "private_module", "private_module_manifest", "phone_compiler_bundle"):
        value = getattr(args, name)
        if value:
            setattr(args, name, value.resolve())
    verifier = load_verifier()
    manifest, libraries = verifier.native_inputs(args.native_manifest)
    if args.min_sdk < int(manifest["api"]):
        parser.error("APK minSDK cannot be below native build API")
    verifier.module_identity(manifest, args.private_module, args.private_module_manifest, args.min_sdk)
    frozen_inputs = {args.native_manifest: sha256(args.native_manifest)}
    if args.private_module:
        frozen_inputs[args.private_module] = sha256(args.private_module)
    if args.private_module_manifest:
        frozen_inputs[args.private_module_manifest] = sha256(args.private_module_manifest)
    output.mkdir(parents=True)
    shutil.copyfile(args.native_manifest, output / "native-input-manifest.json")
    if args.private_module_manifest:
        shutil.copyfile(args.private_module_manifest, output / "private-module-input-manifest.json")
    project = output / "project"
    project.mkdir()
    files = source_files(source)
    hashes = {p.relative_to(source).as_posix(): sha256(p) for p in files}
    (output / "source-hashes.json").write_text(json.dumps(hashes, indent=2), encoding="utf-8")
    for path in files:
        target = project / path.relative_to(source / "Godot")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
    entry = project / "src/android/AndroidGame.tscn"
    if not entry.is_file():
        raise ValueError("Android entry scene missing; wait for source completion before export")
    preset = project / "export_presets.cfg"
    preset.write_text(set_preset(preset.read_text(encoding="utf-8"), args.package, args.min_sdk, args.target_sdk,
        args.debug_network, args.netplay, args.nativeaot), encoding="utf-8")
    bundled = stage_bundled_mods(source, project)
    preset.write_text(include_resource(preset.read_text(encoding="utf-8"), BUNDLED_MODS), encoding="utf-8")
    (output / "bundled-mods.json").write_text(json.dumps(bundled, indent=2), encoding="utf-8")
    project_settings = project / "project.godot"
    project_settings.write_text(android_export_settings(project_settings.read_text(encoding="utf-8"), args.rendering_method), encoding="utf-8")
    if args.phone_compiler_bundle:
        proof = stage_phone_compiler(args.phone_compiler_bundle, project, args.native_manifest)
        settings_text, preset_text = phone_compiler_settings(project_settings.read_text(encoding="utf-8"),
            preset.read_text(encoding="utf-8"))
        project_settings.write_text(settings_text, encoding="utf-8")
        preset.write_text(preset_text, encoding="utf-8")
        frozen_inputs[args.phone_compiler_bundle / "data.zip"] = proof["data_sha256"]
        (output / "phone-compiler-bundle-proof.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
    editor_dir = output / "editor"
    editor_dir.mkdir()
    editor = editor_dir / args.godot.name
    shutil.copy2(args.godot, editor)
    if not (args.godot.parent / "GodotSharp").is_dir():
        raise ValueError("Use the official .NET Godot editor with adjacent GodotSharp directory")
    shutil.copytree(args.godot.parent / "GodotSharp", editor_dir / "GodotSharp")
    (editor_dir / "_sc_").write_text("", encoding="utf-8")
    settings = editor_dir / "editor_data/editor_settings-4.7.tres"
    settings.parent.mkdir()
    settings.write_text('[gd_resource type="EditorSettings" format=3]\n\n[resource]\n' +
        'export/android/java_sdk_path = ' + json.dumps(args.jdk.as_posix()) + '\n' +
        'export/android/android_sdk_path = ' + json.dumps(args.sdk.as_posix()) + '\n' +
        'filesystem/on_save/safe_save = false\n', encoding="utf-8")
    version = install_template(args.templates, project, editor)
    if args.phone_compiler_bundle:
        stage_install_service(project)
    if args.gradle_archive:
        expected = "f397b287023acdba1e9f6fc5ea72d22dd63669d59ed4a289a29b1a76eee151c6"
        if sha256(args.gradle_archive) != expected:
            raise ValueError("Gradle archive must be official gradle-8.11.1-bin.zip with pinned SHA256")
        wrapper = project / "android/build/gradle/wrapper/gradle-wrapper.properties"
        content = wrapper.read_text(encoding="utf-8")
        content = re.sub(r"(?m)^distributionUrl=.*$", "distributionUrl=" + args.gradle_archive.as_uri(), content)
        wrapper.write_text(content, encoding="utf-8")
    cache = (args.cache_root or output / "cache").resolve()
    cache.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(JAVA_HOME=str(args.jdk), ANDROID_HOME=str(args.sdk), ANDROID_SDK_ROOT=str(args.sdk),
        ANDROID_USER_HOME=str(output / "android-user"), GRADLE_USER_HOME=str(cache / "gradle-home"),
        DOTNET_ROOT=str(args.dotnet), DOTNET_CLI_HOME=str(output / "dotnet-home"),
        NUGET_PACKAGES=str(cache / "nuget-packages"), APPDATA=str(output / "appdata"),
        LOCALAPPDATA=str(output / "localappdata"), TEMP=str(output / "temp"), TMP=str(output / "temp"),
        REFII_NO_TESTS="1", REFII_NO_SHARED_COMPILATION="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    # The NativeAOT link step runs the NDK's clang (and llvm-objcopy for split symbols) by name.
    toolchain = [str(ndk_prebuilt(args.ndk) / "bin")] if args.nativeaot else []
    env["PATH"] = os.pathsep.join([str(args.dotnet), str(args.jdk / "bin"), *toolchain, env.get("PATH", "")])
    for key in ("ANDROID_USER_HOME", "DOTNET_CLI_HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "NUGET_PACKAGES", "GRADLE_USER_HOME"):
        Path(env[key]).mkdir(parents=True, exist_ok=True)
    steps = []

    def run(name: str, argv: list, cwd: Path | None = None) -> None:
        log, started = output / (name + ".log"), time.monotonic()
        print(f"{name}: {log}", flush=True)
        argv = [str(a) for a in argv]
        with log.open("w", encoding="utf-8") as stream:
            # Password values are supplied through named environment variables;
            # command logs contain only env:NAME references.
            stream.write("argv: " + json.dumps(argv) + "\n\n")
            stream.flush()
            result = subprocess.run(argv, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        steps.append(dict(step=name, exit_code=result.returncode, seconds=time.monotonic() - started, log=str(log)))
        (output / "build-steps.json").write_text(json.dumps(steps, indent=2), encoding="utf-8")
        if result.returncode:
            raise RuntimeError(f"{name} failed ({result.returncode}); see {log}")

    # A fresh source-only project has no editor assembly yet. Build it before
    # importing C# autoloads; accepting the resulting autoload error after a later
    # successful Android publish would hide a genuine staged-project problem.
    run("managed-editor-build", [executable(args.dotnet, "dotnet"), "build",
        project / "refii-launcher.csproj", "--configuration", "Debug",
        "-p:REFII_NO_TESTS=1", "-p:UseSharedCompilation=false"], project)
    run("godot-version", [editor, "--version"])
    if "4.7.1.stable.mono" not in (output / "godot-version.log").read_text(encoding="utf-8"):
        raise ValueError("Require matching Godot 4.7.1 .NET editor")
    base = output / "refii-base.apk"
    try:
        run("godot-export", [editor, "--headless", "--path", project, "--export-debug", "Android ARM64", base], project)
    finally:
        logs = editor_dir / "editor_data/mono/build_logs"
        if logs.is_dir():
            shutil.copytree(logs, output / "managed-publish-logs")
    errors = export_errors((output / "godot-export.log").read_text(encoding="utf-8", errors="replace"))
    if errors:
        raise ValueError("Godot reported export/publish errors despite exit code: " + "; ".join(errors[:5]))
    if {p.relative_to(source).as_posix(): sha256(p) for p in source_files(source)} != hashes:
        raise ValueError("Source changed during export; retry in a new output after freezing sources")
    if any(sha256(path) != digest for path, digest in frozen_inputs.items()):
        raise ValueError("Native/private manifest or private module changed during export")
    configuration = "ExportDebug"
    if args.nativeaot:
        dll = None
        native_output = project / ".godot/mono/temp/bin" / configuration / NATIVEAOT_RID / "native"
        published_native = native_output / "refii-launcher.so"
        if not published_native.is_file():
            raise ValueError("Fresh NativeAOT publish output missing")
        # Split symbols stay off the APK; keep them with the build for symbolizing device profiles.
        symbols = native_output / "refii-launcher.so.dbg"
        if symbols.is_file():
            (output / "native-symbols").mkdir()
            shutil.copyfile(symbols, output / "native-symbols/librefii-launcher.so.dbg")
    else:
        published_native = None
        dll = project / ".godot/mono/temp/bin" / configuration / "android-arm64/refii-launcher.dll"
        if not dll.is_file():
            raise ValueError("Fresh managed publish output missing")
    packaged = output / "refii-unaligned.apk"
    with zipfile.ZipFile(base) as archive, zipfile.ZipFile(packaged, "w", allowZip64=True) as target:
        original_launch = archive.read("assets/_cl_")
        selected_launch = configure_launch_arguments(original_launch, args.rendering_method, args.gpu_profile)
        launch_report = dict(original_arguments=decode_launch_arguments(original_launch),
            packaged_arguments=decode_launch_arguments(selected_launch),
            gpu_profile=args.gpu_profile,
            launch_bytes_changed=selected_launch != original_launch,
            format="little-endian uint32 count; uint32 UTF-8 byte length then bytes")
        (output / "command-line.bin").write_bytes(selected_launch)
        (output / "command-line.json").write_text(json.dumps(launch_report, indent=2), encoding="utf-8")
        for entry in archive.infolist():
            if entry.filename.startswith("META-INF/") or entry.filename in {"lib/arm64-v8a/" + name for name in libraries}:
                continue
            if private_resource(entry.filename):
                raise ValueError("Base APK contains private/build resource: " + entry.filename)
            write_base_entry(target, entry, selected_launch if entry.filename == "assets/_cl_" else archive.read(entry))
        for name, path in libraries.items():
            target.write(path, "lib/arm64-v8a/" + name, compress_type=zipfile.ZIP_STORED)
    build_tools = args.sdk / "build-tools" / args.build_tools
    final = output / "refii-android.apk"
    run("zipalign", [executable(build_tools, "zipalign"), "-P", "16", "-f", "4", packaged, final])
    key = args.keystore or output / "debug.keystore"
    if not args.keystore:
        env[args.store_pass_env] = env[args.key_pass_env] = "android"
        run("debug-key", [executable(args.jdk / "bin", "keytool"), "-genkeypair", "-keystore", key,
            "-storepass:env", args.store_pass_env, "-keypass:env", args.key_pass_env, "-alias", args.alias,
            "-dname", "CN=Android Debug,O=Android,C=US", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000"])
    elif not env.get(args.store_pass_env):
        raise ValueError(f"Signing requires environment variable {args.store_pass_env}")
    if not env.get(args.key_pass_env):
        env[args.key_pass_env] = env[args.store_pass_env]
    signer = [executable(args.jdk / "bin", "java"), "-jar", build_tools / "lib/apksigner.jar"]
    run("sign", signer + ["sign", "--ks", key, "--ks-key-alias", args.alias,
        "--ks-pass", "env:" + args.store_pass_env, "--key-pass", "env:" + args.key_pass_env, final])
    run("signature-verify", signer + ["verify", "--verbose", "--print-certs", final])
    run("alignment-verify", [executable(build_tools, "zipalign"), "-c", "-P", "16", "4", final])
    with zipfile.ZipFile(final) as archive:
        if archive.read("assets/_cl_") != selected_launch:
            raise ValueError("Signed APK launch arguments differ from the requested renderer/profiling configuration")
        if args.phone_compiler_bundle:
            proof["packaged_data_sha256"] = verify_phone_bundle_archive(archive, proof["data_sha256"])
            proof["apk_sha256"] = sha256(final)
            (output / "phone-compiler-bundle-proof.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
    verify_args = argparse.Namespace(apk=final, native_manifest=args.native_manifest, ndk=args.ndk, sdk=args.sdk,
        output=output / "verification", build_tools=args.build_tools, min_sdk=args.min_sdk, target_sdk=args.target_sdk,
        package=args.package, private_module=args.private_module, private_module_manifest=args.private_module_manifest,
        published_dll=dll, project=project, debug_network=args.debug_network, netplay=args.netplay, phone_installer=bool(args.phone_compiler_bundle),
        nativeaot=args.nativeaot, published_native=published_native)
    result = verifier.audit(verify_args)
    result = dict(apk=str(final), apk_sha256=result["apk_sha256"], apk_bytes=final.stat().st_size,
        managed_runtime=result["managed_runtime"], managed_sha256=result["managed_sha256"],
        native_manifest_sha256=sha256(args.native_manifest),
        source_commit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip(),
        staged_source_count=len(files), source_unchanged_during_export=True, version=version,
        package=args.package, min_sdk=args.min_sdk, target_sdk=args.target_sdk,
        rendering_method=args.rendering_method, gpu_profile=args.gpu_profile, command_line=launch_report,
        debug_network=args.debug_network, netplay=args.netplay, nativeaot=args.nativeaot,
        android_permissions=result["android_permissions"],
        private_module_packaged=False, private_assets_or_saves_packaged=False,
        private_module_identity=result["private_module_identity"],
        verification=str(output / "verification/verification.json"), exact_export_teardown_whitelist=EXPORT_TEARDOWN_WHITELIST,
        debug_build=True, signing_key=str(key), tool_inputs={k: str(getattr(args, k)) for k in
            ("godot", "templates", "dotnet", "jdk", "sdk", "ndk")}, steps=steps)
    (output / "build-report.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
