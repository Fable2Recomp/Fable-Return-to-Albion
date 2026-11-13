"""build-phone-tools.py's data layout and refusals; no NDK, LLVM or device required."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location('build_phone_tools', TOOLS / 'build-phone-tools.py')
phone_tools = importlib.util.module_from_spec(spec)
spec.loader.exec_module(phone_tools)


def touch(path: Path, text: str = 'x') -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8')


class LayDataTests(unittest.TestCase):
    def test_takes_the_arm64_api31_sysroot_resources_and_sdk_headers(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            ndk = root / 'ndk'
            host = ndk / 'toolchains/llvm/prebuilt/linux-x86_64'
            touch(ndk / 'NOTICE')
            touch(host / 'NOTICE')
            touch(host / 'sysroot/usr/include/stdio.h')
            touch(host / 'sysroot/usr/include/aarch64-linux-android/asm/types.h')
            touch(host / 'sysroot/usr/include/arm-linux-androideabi/asm/types.h')
            touch(host / 'sysroot/usr/lib/aarch64-linux-android/31/libc.so')
            touch(host / 'sysroot/usr/lib/aarch64-linux-android/30/libc.so')
            touch(host / 'sysroot/usr/lib/aarch64-linux-android/libc++_shared.so')
            touch(host / 'lib/clang/19/include/arm_neon.h')
            touch(host / 'lib/clang/19/lib/linux/libclang_rt.builtins-aarch64-android.a')
            touch(host / 'lib/clang/19/lib/linux/libclang_rt.builtins-arm-android.a')
            touch(host / 'lib/clang/19/lib/linux/aarch64/libunwind.a')
            touch(host / 'lib/clang/19/lib/linux/aarch64/libomp.so')
            sdk = root / 'sdk'
            touch(sdk / 'LICENSE')
            for name, (folder, includes) in phone_tools.THIRD_PARTY.items():
                for include in includes:
                    touch(sdk / 'thirdparty' / folder / include / 'header.h')
            for relative in phone_tools.LICENSES.values():
                touch(sdk / 'thirdparty' / relative)
            data = root / 'data'
            data.mkdir()

            phone_tools.lay_data(host, data, sdk)

            self.assertTrue((data / 'sysroot/usr/include/aarch64-linux-android/asm/types.h').is_file())
            self.assertFalse((data / 'sysroot/usr/include/arm-linux-androideabi').exists())
            self.assertTrue((data / 'sysroot/usr/lib/aarch64-linux-android/31/libc.so').is_file())
            self.assertFalse((data / 'sysroot/usr/lib/aarch64-linux-android/30').exists())
            self.assertTrue((data / 'sysroot/usr/lib/aarch64-linux-android/libc++_shared.so').is_file())
            self.assertTrue((data / 'resource/include/arm_neon.h').is_file())
            self.assertTrue((data / 'resource/lib/linux/libclang_rt.builtins-aarch64-android.a').is_file())
            self.assertFalse((data / 'resource/lib/linux/libclang_rt.builtins-arm-android.a').exists())
            self.assertTrue((data / 'resource/lib/linux/aarch64/libunwind.a').is_file())
            self.assertFalse((data / 'resource/lib/linux/aarch64/libomp.so').exists())
            self.assertTrue((data / 'sdk/thirdparty/tracy/public/header.h').is_file())
            self.assertTrue((data / 'licenses/NDK-toolchain-NOTICE').is_file())


class RefusalTests(unittest.TestCase):
    def test_needs_clang_and_lld_from_somewhere(self):
        with tempfile.TemporaryDirectory() as temp:
            argv = ['build-phone-tools.py', '--ndk', temp, '--native', temp, '--out', str(Path(temp) / 'out')]
            with patch.object(sys, 'argv', argv), self.assertRaises(SystemExit) as stop:
                phone_tools.main()
            self.assertIn('--llvm-source', str(stop.exception))

    def test_output_must_be_new(self):
        with tempfile.TemporaryDirectory() as temp:
            argv = ['build-phone-tools.py', '--ndk', temp, '--native', temp, '--out', temp, '--tools-from', temp]
            with patch.object(sys, 'argv', argv), self.assertRaises(SystemExit) as stop:
                phone_tools.main()
            self.assertIn('new directory', str(stop.exception))


if __name__ == '__main__':
    unittest.main()
