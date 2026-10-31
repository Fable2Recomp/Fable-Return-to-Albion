#!/usr/bin/env python3
"""Build a signed APK in one step: native runtime, phone compiler bundle, export and checksums.

    python tools/android/build-apk.py --config build-apk.toml --output ../android-local/release-01

Every tool path comes from the config file (see build-apk.example.toml beside this
script); every output goes under --output, which must be new and outside the
checkout. The steps, each a script of its own that can also be run by hand:

  1. templates  tools/android/godot-patches/build-templates.py, only when the config's
                `templates` directory does not exist yet and `godot_source` and
                `official_templates` say how to make it
  2. native     tools/android/build-native.py --rexnet       -> <output>/native
  3. bundle     tools/android/pack-phone-bundle.py           -> <output>/compiler-bundle
  4. export     tools/android/export-apk.py (signs and verifies) -> <output>/apk
  5. checksums  <output>/SHA256SUMS.txt and <output>/build-apk.json

Signing passwords are read from `passwords_json` (a JSON object with
REFII_ANDROID_STORE_PASSWORD and REFII_ANDROID_KEY_PASSWORD) or from those
environment variables, and are passed to the exporter through its environment;
they are never printed or written. Without `keystore`, the exporter makes a new
local debug key, and an APK signed with it cannot update one signed with another.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import tomllib

REPO = Path(__file__).resolve().parents[2]
TOOLS = REPO / 'tools/android'
PASSWORD_ENV = ('REFII_ANDROID_STORE_PASSWORD', 'REFII_ANDROID_KEY_PASSWORD')
REQUIRED = ('godot', 'templates', 'dotnet', 'jdk', 'sdk', 'ndk', 'host_codegen', 'phone_tools_from')


def load_config(path: Path) -> dict:
    """The config's values, with every path made absolute against the config's own folder."""
    config = tomllib.loads(path.read_text(encoding='utf-8'))
    missing = [key for key in REQUIRED if key not in config]
    if missing:
        raise ValueError('build-apk config is missing: ' + ', '.join(missing))
    base = path.resolve().parent
    for key, value in list(config.items()):
        if isinstance(value, str) and key not in ('package', 'alias', 'rendering_method', 'game_version'):
            config[key] = str((base / Path(value).expanduser()).resolve())
    return config


def plan(config: dict, output: Path, native: Path | None) -> list[tuple[str, list[str]]]:
    """The steps' commands, in order: (name, argv)."""
    py = [sys.executable, '-B']
    steps = []
    templates = Path(config['templates'])
    if not templates.exists():
        if not ('godot_source' in config and 'official_templates' in config):
            raise ValueError(f'templates {templates} does not exist; set godot_source and official_templates to build it')
        steps.append(('templates', py + [str(TOOLS / 'godot-patches/build-templates.py'),
                                         '--godot-source', config['godot_source'],
                                         '--official-templates', config['official_templates'],
                                         '--out', str(templates)]))
    native_dir = native or output / 'native'
    if native is None:
        cmd = py + [str(TOOLS / 'build-native.py'), '--ndk', config['ndk'], '--output', str(native_dir),
                    '--host-codegen', config['host_codegen'], '--game-version', config.get('game_version', 'Fable2TU1'),
                    '--jobs', str(config.get('jobs', os.cpu_count() or 4)), '--rexnet']
        for key in ('sdk_source', 'zlib_source'):
            if key in config:
                cmd += ['--' + key.replace('_', '-'), config[key]]
        steps.append(('native', cmd))
    bundle = output / 'compiler-bundle'
    steps.append(('bundle', py + [str(TOOLS / 'pack-phone-bundle.py'), '--native', str(native_dir),
                                  '--tools-from', config['phone_tools_from'], '--out', str(bundle)]))
    export = py + [str(TOOLS / 'export-apk.py'), '--output', str(output / 'apk'),
                   '--godot', config['godot'], '--templates', config['templates'], '--dotnet', config['dotnet'],
                   '--jdk', config['jdk'], '--sdk', config['sdk'], '--ndk', config['ndk'],
                   '--native-manifest', str(bundle / 'native-manifest.json'), '--phone-compiler-bundle', str(bundle),
                   '--package', config.get('package', 'org.refii.androidpresentationprobe'),
                   '--min-sdk', str(config.get('min_sdk', 31)), '--target-sdk', str(config.get('target_sdk', 36)),
                   '--rendering-method', config.get('rendering_method', 'mobile'), '--netplay', '--nativeaot']
    if 'cache_root' in config:
        export += ['--cache-root', config['cache_root']]
    if 'keystore' in config:
        export += ['--keystore', config['keystore'], '--alias', config.get('alias', 'androiddebugkey')]
    steps.append(('export', export))
    return steps


def signing_env(config: dict) -> dict:
    """The environment for the exporter, with the signing passwords when a keystore is set."""
    env = dict(os.environ)
    if 'keystore' not in config:
        return env
    if 'passwords_json' in config:
        values = json.loads(Path(config['passwords_json']).read_text(encoding='utf-8'))
        env.update({key: values[key] for key in PASSWORD_ENV if key in values})
    if not env.get(PASSWORD_ENV[0]):
        raise ValueError(f'keystore is set but neither passwords_json nor {PASSWORD_ENV[0]} gives its password')
    return env


def sha256(path: Path) -> str:
    with path.open('rb') as handle:
        return hashlib.file_digest(handle, 'sha256').hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='a new directory outside the checkout')
    parser.add_argument('--native', type=Path, help='reuse an earlier build-native.py output instead of building one')
    parser.add_argument('--dry-run', action='store_true', help='print the steps and stop')
    args = parser.parse_args()

    config = load_config(args.config)
    output = args.output.resolve()
    if output.is_relative_to(REPO):
        raise ValueError('--output must be outside the checkout')
    steps = plan(config, output, args.native.resolve() if args.native else None)
    if args.dry_run:
        for name, cmd in steps:
            print(f'{name}: ' + ' '.join(cmd))
        return 0
    if output.exists():
        raise ValueError(f'{output} exists; use a new directory')
    env = signing_env(config)
    output.mkdir(parents=True)
    logs = output / 'logs'
    logs.mkdir()
    record = {'commit': subprocess.run(['git', '-C', str(REPO), 'rev-parse', 'HEAD'], capture_output=True,
                                       text=True).stdout.strip(), 'steps': []}
    for name, cmd in steps:
        started = time.time()
        print(f'[{time.strftime("%H:%M:%S")}] {name} (log: {logs / (name + ".log")})', flush=True)
        with (logs / f'{name}.log').open('w', encoding='utf-8') as log:
            result = subprocess.run(cmd, cwd=REPO, env=env if name == 'export' else None,
                                    stdout=log, stderr=subprocess.STDOUT)
        record['steps'].append({'step': name, 'seconds': round(time.time() - started), 'exit': result.returncode})
        if result.returncode:
            print(f'{name} failed (exit {result.returncode}); the end of its log:', file=sys.stderr)
            print(''.join((logs / f'{name}.log').read_text(encoding='utf-8', errors='replace').splitlines(True)[-20:]),
                  file=sys.stderr)
            (output / 'build-apk.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
            return result.returncode

    apk = output / 'apk/refii-android.apk'
    shutil.copyfile(apk, output / 'refii-android.apk')
    record['apk_sha256'] = sha256(apk)
    record['bundle_proof'] = json.loads((output / 'compiler-bundle/bundle-proof.json').read_text(encoding='utf-8'))
    (output / 'SHA256SUMS.txt').write_text(f"{record['apk_sha256']}  refii-android.apk\n", encoding='utf-8')
    (output / 'build-apk.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
    print(f"APK: {output / 'refii-android.apk'}\nSHA-256: {record['apk_sha256']}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
