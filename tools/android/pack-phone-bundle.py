#!/usr/bin/env python3
"""Refresh the phone compiler bundle for this checkout's native runtime.

The bundle is what lets the phone generate, compile and link the game itself
(`export-apk.py --phone-compiler-bundle`). Its executable tools (the ARM64
Clang, lld and ReXGlue generator, and the phone bridge) are prebuilt and taken
unchanged from an earlier bundle (`--tools-from`), with its NDK sysroot, Clang
resources and recipe pins; this checkout and a `build-native.py` output are laid
over them: the runtime libraries, the public headers, the payload hook sources the
phone compiles and the version configs. The data ZIP is rehashed and `bundle-proof.json` binds it to the new
`native-manifest.json`. No game files, generated source or game module are read
or written.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from android_common import inspect_elf, private_resource  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
HEADER_SUFFIXES = ('.h', '.hpp', '.hxx', '.inc', '.inl', '.ipp', '.def', '.tcc')


def sha(path: Path) -> str:
    """The SHA-256 of a file."""
    with path.open('rb') as handle:
        return hashlib.file_digest(handle, 'sha256').hexdigest()


def host_abi(repo: Path) -> int:
    """RefiiHostBridgeAbiVersion as host_bridge.cpp returns it."""
    text = (repo / 'src/hostbridge/src/host_bridge.cpp').read_text(encoding='utf-8')
    match = re.search(r'RefiiHostBridgeAbiVersion\(void\)\s*\{\s*return\s+(\d+);', text)
    if not match:
        raise ValueError('RefiiHostBridgeAbiVersion not found in host_bridge.cpp')
    return int(match.group(1))


def copy_headers(source: Path, target: Path) -> None:
    """Overlays the public text headers under source; a private resource name stops the run."""
    for file in source.rglob('*'):
        if file.is_file() and file.suffix.lower() in HEADER_SUFFIXES:
            if private_resource(file.name):
                raise ValueError('Private header selected: ' + str(file))
            dest = target / file.relative_to(source)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(file, dest)


def pack(repo: Path, native: Path, tools_from: Path, out: Path, built_from: Path | None = None) -> dict:
    """Writes the refreshed bundle to out (which must not exist) and returns its proof. built_from is the
    checkout the native build compiled, when not this one; its hook sources must match this checkout's."""
    if out.exists():
        raise ValueError('The bundle output must be a new directory: ' + str(out))
    for required in ('data/bundle.json', 'native', 'native-manifest.json'):
        if not (tools_from / required).exists():
            raise ValueError(f'{tools_from} is not a phone compiler bundle (no {required})')
    build = native / 'build'
    if not (native / 'native-manifest.json').is_file() or not (build / 'compile_commands.json').is_file():
        raise ValueError(f'{native} is not a build-native.py output (native-manifest.json, build/compile_commands.json)')

    shutil.copytree(tools_from / 'data', out / 'data')
    shutil.copytree(tools_from / 'native', out / 'native')
    data, tools = out / 'data', out / 'native'
    for relative in ('src', 'tools/android', 'rexglue-net/shadow/rexglue-sdk/include'):
        copy_headers(repo / relative, data / relative)
    shadow = build / 'rexglue-sdk-shadow'
    copy_headers(shadow / 'include', data / 'sdk/include')
    copy_headers(build / 'rexglue-sdk/include', data / 'sdk-generated/include')
    copy_headers(build / 'rexglue-sdk/thirdparty/sdl3/include-revision',
                 data / 'sdk-generated/thirdparty/sdl3/include-revision')

    # The payload hooks the phone compiles beside the generated game code, in the build's order.
    recipe = json.loads((data / 'bundle.json').read_text(encoding='utf-8'))
    commands = json.loads((build / 'compile_commands.json').read_text(encoding='utf-8'))
    hooks = []
    for row in commands:
        if 'refii_hooks.dir' not in row.get('command', '') or not row['file'].endswith('.cpp'):
            continue
        source = Path(row['file']).resolve()
        relative = next((source.relative_to(root).as_posix() for root in (repo.resolve(), (built_from or repo).resolve())
                         if source.is_relative_to(root)), None)
        if relative is None or not (repo / relative).is_file():
            raise ValueError('Payload hook outside this checkout: ' + row['file'])
        hooks.append(relative)
        (data / relative).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(repo / relative, data / relative)
    if not hooks:
        raise ValueError('No payload hook compile commands in ' + str(build / 'compile_commands.json'))
    recipe['Hooks'] = hooks + ['sdk/thirdparty/fmt/src/format.cpp']
    (data / 'sdk/thirdparty/fmt/src').mkdir(parents=True, exist_ok=True)
    shutil.copyfile(shadow / 'thirdparty/fmt/src/format.cc', data / 'sdk/thirdparty/fmt/src/format.cpp')
    shutil.copyfile(repo / 'src/payload/src/payload_export.cpp', data / 'src/payload/src/payload_export.cpp')
    for version in ('Fable2', 'Fable2TU1'):
        for name in ('refii_manifest.toml', 'refii_config.toml'):
            (data / 'versions' / version).mkdir(parents=True, exist_ok=True)
            shutil.copyfile(repo / 'versions' / version / name, data / 'versions' / version / name)

    # The runtime libraries from this build; the tools stay as they came.
    manifest = json.loads((native / 'native-manifest.json').read_text(encoding='utf-8'))
    for row in manifest['libraries']:
        shutil.copyfile(native / row['path'], tools / Path(row['path']).name)
    for name, expected in manifest['header_identity'].items():
        relative = name.replace('\\', '/')
        if '/rexglue-sdk/include/' in relative and '/shadow/' not in relative:
            relative = 'sdk/include/' + relative.split('/rexglue-sdk/include/', 1)[1]
        if sha(data / relative) != expected:
            raise ValueError('Runtime/header mismatch: ' + relative)
    for file in sorted(tools.iterdir()):
        info = inspect_elf(file.read_bytes())
        if info['soname'] != file.name or not info['load_alignment_16k'] or info['rpath']:
            raise ValueError('Tool ELF contract (SONAME, 16 KiB alignment, no RPATH): ' + file.name)
        if not any(Path(row['path']).name == file.name for row in manifest['libraries']):
            manifest['libraries'].append(dict(path='native/' + file.name, sha256=sha(file), bytes=file.stat().st_size,
                                              soname=info['soname'], needed=info['needed'], load_alignment=16384))
    (out / 'native-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')

    recipe['Files'] = {p.relative_to(data).as_posix(): sha(p) for p in sorted(data.rglob('*'))
                       if p.is_file() and p.name != 'bundle.json'}
    recipe['NativeFiles'] = {p.name: sha(p) for p in sorted(tools.iterdir())}
    (data / 'bundle.json').write_text(json.dumps(recipe, indent=2), encoding='utf-8')
    with zipfile.ZipFile(out / 'data.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for file in sorted(data.rglob('*')):
            if file.is_file():
                archive.write(file, file.relative_to(data).as_posix())
    proof = dict(schema=1, data_sha256=sha(out / 'data.zip'), native_manifest_sha256=sha(out / 'native-manifest.json'),
                 data_files=len(recipe['Files']), hooks=len(hooks), private_module_excluded=True,
                 contains_game_files=False, host_abi=host_abi(repo), executable_tools_reused_from=str(tools_from))
    (out / 'bundle-proof.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
    return proof


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--native', type=Path, required=True, help='a build-native.py --output directory')
    parser.add_argument('--tools-from', type=Path, required=True, help='an earlier phone compiler bundle')
    parser.add_argument('--out', type=Path, required=True, help='a new directory for the bundle')
    parser.add_argument('--built-from', type=Path, help='the checkout --native was built from, if not this one')
    args = parser.parse_args()
    proof = pack(REPO, args.native.resolve(), args.tools_from.resolve(), args.out.resolve(), args.built_from)
    print(f"Phone compiler bundle: {args.out} ({proof['data_files']} files, {proof['hooks']} hooks, host ABI {proof['host_abi']})")
    return 0


if __name__ == '__main__':
    sys.exit(main())
