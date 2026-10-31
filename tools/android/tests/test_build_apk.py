"""build-apk.py's plan and config, and pack-phone-bundle.py's checks; no tools, keys or device required."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, TOOLS / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build_apk = load('build_apk', 'build-apk.py')
packer = load('pack_phone_bundle', 'pack-phone-bundle.py')

CONFIG = '''
godot = "tools/godot"
templates = "templates"
dotnet = "/dotnet"
jdk = "/jdk"
sdk = "/sdk"
ndk = "/ndk"
host_codegen = "/rexglued"
phone_tools_from = "bundle"
package = "org.example.refii"
'''


class BuildApkTests(unittest.TestCase):
    def config(self, temp, extra=''):
        path = Path(temp) / 'build-apk.toml'
        path.write_text(CONFIG + extra, encoding='utf-8')
        (Path(temp) / 'templates').mkdir(exist_ok=True)
        return build_apk.load_config(path)

    def test_relative_paths_are_read_against_the_config_and_names_stay_names(self):
        with tempfile.TemporaryDirectory() as temp:
            config = self.config(temp)
            self.assertEqual(config['godot'], str((Path(temp) / 'tools/godot').resolve()))
            self.assertEqual(config['ndk'], '/ndk')
            self.assertEqual(config['package'], 'org.example.refii')

    def test_a_missing_tool_is_named(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'build-apk.toml'
            path.write_text('godot = "/g"\n', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'host_codegen'):
                build_apk.load_config(path)

    def test_the_release_build_runs_native_bundle_and_export_with_the_release_flags(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / 'out'
            steps = dict(build_apk.plan(self.config(temp), out, None))
            self.assertEqual(list(steps), ['native', 'bundle', 'export'])
            self.assertIn('--rexnet', steps['native'])
            self.assertEqual(steps['bundle'][steps['bundle'].index('--native') + 1], str(out / 'native'))
            export = steps['export']
            for flag in ('--netplay', '--nativeaot'):
                self.assertIn(flag, export)
            self.assertEqual(export[export.index('--phone-compiler-bundle') + 1], str(out / 'compiler-bundle'))
            self.assertEqual(export[export.index('--native-manifest') + 1], str(out / 'compiler-bundle/native-manifest.json'))
            self.assertEqual(export[export.index('--rendering-method') + 1], 'mobile')
            self.assertEqual(export[export.index('--min-sdk') + 1], '31')
            self.assertNotIn('--keystore', export)

    def test_a_reused_native_build_skips_the_native_step(self):
        with tempfile.TemporaryDirectory() as temp:
            steps = dict(build_apk.plan(self.config(temp), Path(temp) / 'out', Path('/earlier/native')))
            self.assertNotIn('native', steps)
            self.assertEqual(steps['bundle'][steps['bundle'].index('--native') + 1], '/earlier/native')

    def test_missing_templates_are_built_only_when_the_config_says_how(self):
        with tempfile.TemporaryDirectory() as temp:
            config = self.config(temp)
            os.rmdir(config['templates'])
            with self.assertRaisesRegex(ValueError, 'godot_source'):
                build_apk.plan(config, Path(temp) / 'out', None)
            config.update(godot_source='/godot', official_templates='/official')
            self.assertEqual(build_apk.plan(config, Path(temp) / 'out', None)[0][0], 'templates')

    def test_signing_passwords_come_from_the_json_and_are_required_with_a_keystore(self):
        with tempfile.TemporaryDirectory() as temp:
            secrets = Path(temp) / 'passwords.json'
            secrets.write_text(json.dumps({'REFII_ANDROID_STORE_PASSWORD': 's', 'REFII_ANDROID_KEY_PASSWORD': 'k'}))
            config = self.config(temp, 'keystore = "release.keystore"\npasswords_json = "passwords.json"\n')
            with patch.dict(os.environ, {}, clear=True):
                env = build_apk.signing_env(config)
                self.assertEqual((env['REFII_ANDROID_STORE_PASSWORD'], env['REFII_ANDROID_KEY_PASSWORD']), ('s', 'k'))
                del config['passwords_json']
                with self.assertRaisesRegex(ValueError, 'REFII_ANDROID_STORE_PASSWORD'):
                    build_apk.signing_env(config)
            export = dict(build_apk.plan(config, Path(temp) / 'out', None))['export']
            self.assertEqual(export[export.index('--keystore') + 1], str((Path(temp) / 'release.keystore').resolve()))


class PackPhoneBundleTests(unittest.TestCase):
    def test_the_host_abi_is_read_from_the_bridge_source(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp) / 'src/hostbridge/src/host_bridge.cpp'
            source.parent.mkdir(parents=True)
            source.write_text('REFII_HOST_ABI_EXPORT int RefiiHostBridgeAbiVersion(void) { return 228; }\n')
            self.assertEqual(packer.host_abi(Path(temp)), 228)
        self.assertGreater(packer.host_abi(TOOLS.parents[1]), 0)

    def test_it_refuses_an_existing_output_and_inputs_that_are_not_a_bundle_or_a_build(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            with self.assertRaisesRegex(ValueError, 'new directory'):
                packer.pack(temp, temp, temp, temp)
            with self.assertRaisesRegex(ValueError, 'not a phone compiler bundle'):
                packer.pack(temp, temp, temp, temp / 'out')
            (temp / 'old/data').mkdir(parents=True)
            (temp / 'old/data/bundle.json').write_text('{}')
            (temp / 'old/native').mkdir()
            (temp / 'old/native-manifest.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'not a build-native.py output'):
                packer.pack(temp, temp / 'native', temp / 'old', temp / 'out')


if __name__ == '__main__':
    unittest.main()
