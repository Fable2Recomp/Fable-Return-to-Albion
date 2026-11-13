#!/usr/bin/env python3
"""Build the phone compiler's tools from source, in the shape of a phone compiler bundle.

The phone generates, compiles and links its own game module (docs/android.md). Its tools are built
here: the ReXGlue generator and the phone bridge from this checkout (cmake/refii_android.cmake,
REFII_ANDROID_PHONE_TOOLS), and Clang and LLD from an LLVM source tree at the NDK's own LLVM
revision. The NDK's sysroot and Clang resources and the SDK's third-party headers are laid beside
them. The output is what pack-phone-bundle.py --tools-from (build-apk.toml's phone_tools_from)
takes; pack-phone-bundle.py lays this checkout's headers, hooks and runtime over it.

Needs NDK r28c, CMake 3.25+, Ninja, a build-native.py output of this checkout (its build tree is
configured again with the phone tools on) and, for Clang and LLD, llvm-project at
tools/android/NATIVE.md's pin. No download happens here.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from android_common import inspect_elf, sha256  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
API = 31
NDK_REVISION = '28.2.13676358'
LLVM_PIN = '97a699bf4812a18fb657c2779f5296a4ab2694d2'
TOOLS = ('librefii_clang.so', 'librefii_lld.so', 'librefii_codegen.so', 'librefii_phone_bridge.so')
# The SDK's third-party headers the hooks include, by bundle folder: (SDK folder, include folders).
THIRD_PARTY = {
    'fmt': ('fmt', ['include']), 'spdlog': ('spdlog', ['include']), 'simde': ('simde', ['simde']),
    'sdl3': ('sdl3', ['include']), 'vulkan-headers': ('vulkan-headers', ['include']),
    'vulkan-memory-allocator': ('vulkan-memory-allocator', ['include']),
    'tomlplusplus': ('tomlplusplus', ['include']), 'spirv-tools': ('spirv-tools', ['include']),
    'tracy': ('tracy', ['public']),
}
LICENSES = {
    'fmt-LICENSE': 'fmt/LICENSE', 'spdlog-LICENSE': 'spdlog/LICENSE', 'simde-COPYING': 'simde/COPYING',
    'sdl3-LICENSE.txt': 'sdl3/LICENSE.txt', 'vulkan-headers-LICENSE.md': 'vulkan-headers/LICENSE.md',
    'vulkan-memory-allocator-LICENSE.txt': 'vulkan-memory-allocator/LICENSE.txt',
    'tomlplusplus-LICENSE': 'tomlplusplus/LICENSE', 'spirv-tools-LICENSE': 'spirv-tools/LICENSE',
    'tracy-LICENSE': 'tracy/LICENSE', 'xxHash-LICENSE': 'xxHash/LICENSE',
}
OTHER_ABIS = ('arm-linux-androideabi', 'i686-linux-android', 'x86_64-linux-android', 'riscv64-linux-android')


def run(command: list[str], log: Path, env: dict | None = None, cwd: Path | None = None) -> None:
    """Runs a step with its output in log; a failure names the log."""
    with log.open('w', encoding='utf-8') as stream:
        status = subprocess.run([str(part) for part in command], stdout=stream, stderr=subprocess.STDOUT,
                                env={**os.environ, **(env or {})}, cwd=cwd).returncode
    if status != 0:
        raise RuntimeError(f'{command[0]} exited {status}; see {log}')


def prebuilt(ndk: Path) -> Path:
    """The NDK's one host toolchain folder."""
    hosts = [path for path in (ndk / 'toolchains/llvm/prebuilt').iterdir() if (path / 'bin').is_dir()]
    if len(hosts) != 1:
        raise ValueError('Expected one NDK host toolchain in ' + str(ndk))
    return hosts[0]


def strip(ndk_host: Path, source: Path, target: Path) -> None:
    """Copies an executable without its debug sections (the APK's tools are stripped)."""
    subprocess.run([str(ndk_host / 'bin/llvm-strip'), '--strip-unneeded', '-o', str(target), str(source)], check=True)


def build_checkout_tools(native: Path, ndk_host: Path, tools: Path, logs: Path, jobs: int) -> None:
    """The generator and the bridge, from the build-native.py output's tree with the phone tools on."""
    build = native / 'build'
    if not (build / 'CMakeCache.txt').is_file():
        raise ValueError(f'{native} is not a build-native.py output (no build/CMakeCache.txt)')
    run(['cmake', '-S', REPO, '-B', build, '-DREFII_ANDROID_PHONE_TOOLS=ON'], logs / 'tools-configure.log')
    # RexNet's crates link through the NDK's bare clang (build-native.py says why).
    run(['cmake', '--build', build, '--target', 'refii_phone_codegen', 'refii_phone_bridge', '--parallel', str(jobs)],
        logs / 'tools-build.log', env={'CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS':
                                       f'-Clink-arg=--target=aarch64-linux-android{API}'})
    for name in ('librefii_codegen.so', 'librefii_phone_bridge.so'):
        strip(ndk_host, build / 'phone-tools' / name, tools / name)


def build_llvm(llvm: Path, ndk: Path, ndk_host: Path, work: Path, tools: Path, logs: Path, jobs: int) -> None:
    """Clang and LLD for the phone: the host's table generators first, then an ARM64 cross build."""
    revision = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=llvm, capture_output=True, text=True).stdout.strip()
    if revision and revision != LLVM_PIN:
        raise ValueError(f'{llvm} is at {revision}, not the pinned {LLVM_PIN}')
    host = work / 'llvm-host'
    common = ['-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', '-DLLVM_TARGETS_TO_BUILD=AArch64',
              '-DLLVM_INCLUDE_TESTS=OFF', '-DLLVM_INCLUDE_EXAMPLES=OFF', '-DLLVM_INCLUDE_BENCHMARKS=OFF',
              '-DLLVM_ENABLE_ZLIB=OFF', '-DLLVM_ENABLE_ZSTD=OFF', '-DLLVM_ENABLE_LIBXML2=OFF',
              '-DLLVM_ENABLE_TERMINFO=OFF', '-DCLANG_ENABLE_ARCMT=OFF', '-DCLANG_ENABLE_STATIC_ANALYZER=OFF']
    # The host build uses the host's C++ library, and this LLVM's headers take <cstdint> for granted
    # (newer libstdc++ no longer brings it in with <string>).
    run(['cmake', '-S', llvm / 'llvm', '-B', host, *common, '-DLLVM_ENABLE_PROJECTS=clang',
         f'-DCMAKE_C_COMPILER={ndk_host / "bin/clang"}', f'-DCMAKE_CXX_COMPILER={ndk_host / "bin/clang++"}',
         '-DCMAKE_CXX_FLAGS=-include cstdint'],
        logs / 'llvm-host-configure.log')
    run(['cmake', '--build', host, '--target', 'llvm-tblgen', 'clang-tblgen', '--parallel', str(jobs)],
        logs / 'llvm-host-build.log')
    cross = work / 'llvm-android'
    target = f'aarch64-linux-android{API}'
    # Every executable is named like a library in the APK, and the bundle checks each tool's SONAME.
    run(['cmake', '-S', llvm / 'llvm', '-B', cross, *common, '-DLLVM_ENABLE_PROJECTS=clang;lld',
         f'-DCMAKE_TOOLCHAIN_FILE={ndk / "build/cmake/android.toolchain.cmake"}', '-DANDROID_ABI=arm64-v8a',
         f'-DANDROID_PLATFORM=android-{API}', '-DANDROID_STL=c++_shared',
         f'-DLLVM_HOST_TRIPLE={target}', f'-DLLVM_DEFAULT_TARGET_TRIPLE={target}',
         f'-DLLVM_TABLEGEN={host / "bin/llvm-tblgen"}', f'-DCLANG_TABLEGEN={host / "bin/clang-tblgen"}',
         '-DCMAKE_EXE_LINKER_FLAGS=-Wl,-soname,librefii_clang.so -Wl,-z,max-page-size=16384 -Wl,-z,now'],
        logs / 'llvm-android-configure.log')
    run(['cmake', '--build', cross, '--target', 'clang', 'lldELF', '--parallel', str(jobs)], logs / 'llvm-android-build.log')
    clang = next(path for path in sorted((cross / 'bin').glob('clang-*')) if re.fullmatch(r'clang-\d+', path.name))
    strip(ndk_host, clang, tools / 'librefii_clang.so')
    # LLD with its ELF driver alone (tools/android/phone-tools/lld_main.cpp): the name it runs under
    # names no LLD flavor.
    libraries = sorted(str(path) for path in (cross / 'lib').glob('*.a'))
    lld = work / 'librefii_lld.so'
    run([ndk_host / 'bin/clang++', f'--target={target}', '-std=c++17', '-O2', '-fPIE', '-pie',
         f'-I{llvm / "lld/include"}', f'-I{llvm / "llvm/include"}', f'-I{cross / "include"}',
         REPO / 'tools/android/phone-tools/lld_main.cpp', '-Wl,--start-group', *libraries, '-Wl,--end-group',
         '-nostdlib++', '-lc++_shared', '-Wl,-soname,librefii_lld.so',
         '-Wl,-z,max-page-size=16384', '-Wl,-z,now', '-o', lld], logs / 'lld-link.log')
    strip(ndk_host, lld, tools / 'librefii_lld.so')


def lay_data(ndk_host: Path, data: Path, sdk: Path) -> None:
    """The bundle's data that is neither this checkout's nor built: the NDK's ARM64 sysroot for API 31,
    its Clang resources, and the SDK's third-party headers and licences."""
    sysroot = ndk_host / 'sysroot/usr'
    shutil.copytree(sysroot / 'include', data / 'sysroot/usr/include',
                    ignore=lambda folder, names: [n for n in names if n in OTHER_ABIS])
    libs = sysroot / 'lib/aarch64-linux-android'
    target = data / 'sysroot/usr/lib/aarch64-linux-android'
    shutil.copytree(libs / str(API), target / str(API))
    for file in libs.iterdir():
        if file.is_file():
            shutil.copyfile(file, target / file.name)
    resources = next((ndk_host / 'lib/clang').iterdir())
    shutil.copytree(resources / 'include', data / 'resource/include')
    linux = data / 'resource/lib/linux'
    linux.mkdir(parents=True)
    for file in (resources / 'lib/linux').iterdir():
        if file.is_file() and 'aarch64' in file.name:
            shutil.copyfile(file, linux / file.name)
    if (resources / 'lib/linux/aarch64').is_dir():
        shutil.copytree(resources / 'lib/linux/aarch64', linux / 'aarch64',
                        ignore=lambda folder, names: [n for n in names if n.startswith(('libFuzzer', 'libomp', 'lldb'))])
    for name, (folder, includes) in THIRD_PARTY.items():
        for include in includes:
            source = sdk / 'thirdparty' / folder / include
            if not source.is_dir():
                raise ValueError('Missing SDK third-party headers: ' + str(source))
            shutil.copytree(source, data / 'sdk/thirdparty' / name / include)
    licences = data / 'licenses'
    licences.mkdir()
    for name, relative in LICENSES.items():
        shutil.copyfile(sdk / 'thirdparty' / relative, licences / name)
    shutil.copyfile(sdk / 'LICENSE', licences / 'ReXGlue-LICENSE')
    ndk = ndk_host.parents[3]
    shutil.copyfile(ndk / 'NOTICE', licences / 'NDK-NOTICE')
    shutil.copyfile(ndk_host / 'NOTICE', licences / 'NDK-toolchain-NOTICE')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--ndk', type=Path, required=True, help='NDK r28c')
    parser.add_argument('--native', type=Path, required=True, help='a build-native.py --output of this checkout')
    parser.add_argument('--llvm-source', type=Path, help=f'llvm-project at {LLVM_PIN}; without it Clang and LLD '
                        'are taken from --tools-from')
    parser.add_argument('--tools-from', type=Path, help='an earlier phone compiler bundle, for Clang and LLD only')
    parser.add_argument('--work', type=Path, help='where LLVM builds (default: <out>-work)')
    parser.add_argument('--out', type=Path, required=True, help='a new directory')
    parser.add_argument('--jobs', type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()
    out = args.out.resolve()
    if out.exists():
        raise SystemExit('The output must be a new directory: ' + str(out))
    if not args.llvm_source and not args.tools_from:
        raise SystemExit('Clang and LLD need --llvm-source (or --tools-from to reuse an earlier bundle\'s)')
    ndk = args.ndk.resolve()
    if NDK_REVISION not in (ndk / 'source.properties').read_text(encoding='utf-8'):
        raise SystemExit(f'{ndk} is not NDK {NDK_REVISION}')
    ndk_host = prebuilt(ndk)
    sdk = REPO / 'rexglue-net/rexglue-sdk'
    tools, data, logs = out / 'native', out / 'data', out / 'logs'
    for folder in (tools, data, logs):
        folder.mkdir(parents=True)

    build_checkout_tools(args.native.resolve(), ndk_host, tools, logs, args.jobs)
    if args.llvm_source:
        build_llvm(args.llvm_source.resolve(), ndk, ndk_host, (args.work or Path(str(out) + '-work')).resolve(),
                   tools, logs, args.jobs)
    else:
        for name in ('librefii_clang.so', 'librefii_lld.so'):
            shutil.copy2(args.tools_from.resolve() / 'native' / name, tools / name)
    lay_data(ndk_host, data, sdk)

    libraries = []
    for name in TOOLS:
        file = tools / name
        info = inspect_elf(file.read_bytes())
        if info['soname'] != name or not info['load_alignment_16k'] or info['rpath']:
            raise SystemExit(f'Tool ELF contract (SONAME, 16 KiB alignment, no RPATH): {name}')
        libraries.append(dict(path='native/' + name, sha256=sha256(file), bytes=file.stat().st_size,
                              soname=info['soname'], needed=info['needed'], load_alignment=16384))
    sdk_pin = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=sdk, capture_output=True, text=True).stdout.strip()
    recipe = dict(Schema=1, Abi='arm64-v8a', Api=API, Ndk=NDK_REVISION, SdkPin=sdk_pin, Includes=[], Hooks=[],
                  Fable3Includes=[], Fable3Hooks=[], Files={}, NativeFiles={})
    (data / 'bundle.json').write_text(json.dumps(recipe, indent=2), encoding='utf-8')
    (out / 'native-manifest.json').write_text(json.dumps(dict(
        schema_version=1, abi='arm64-v8a', api=API, libraries=libraries,
        llvm=LLVM_PIN if args.llvm_source else 'reused from ' + str(args.tools_from)), indent=2), encoding='utf-8')
    print(f'Phone compiler tools: {out} (SDK {sdk_pin[:12]}, Clang and LLD '
          f'{"built" if args.llvm_source else "reused"})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
