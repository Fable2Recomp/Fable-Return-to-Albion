"""Public bundle staging boundary tests; no game, compiler or Android device required."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location('phone_export', Path(__file__).resolve().parents[1]/'export-apk.py')
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


class PhoneCompilerExportTests(unittest.TestCase):
    """Exercise the full verified public ZIP -> staged Android project boundary."""

    def test_service_template_staging_and_exact_permission_boundary(self):
        with tempfile.TemporaryDirectory() as temp:
            project = Path(temp)
            manifest = project/'android/build/src/main/AndroidManifest.xml'
            manifest.parent.mkdir(parents=True)
            manifest.write_text('<manifest xmlns:android="http://schemas.android.com/apk/res/android"><application/></manifest>')
            source = project/'src/android/installer'
            source.mkdir(parents=True)
            for name in ('RefiiInstallPlugin.java', 'RefiiInstallService.java', 'RefiiPlayService.java'):
                (source/name).write_text('public synthetic fixture')
            exporter.stage_install_service(project)
            android = '{http://schemas.android.com/apk/res/android}'
            root = ET.parse(manifest).getroot()
            service = root.find('application/service')
            self.assertEqual(service.get(android+'exported'), 'false')
            self.assertEqual(service.get(android+'stopWithTask'), 'false')
            self.assertEqual(service.get(android+'foregroundServiceType'), 'dataSync')
            play = root.findall('application/service')[1]
            self.assertEqual(play.get(android+'name'), 'com.godot.game.refii.RefiiPlayService')
            self.assertEqual(play.get(android+'exported'), 'false')
            self.assertEqual(play.get(android+'stopWithTask'), 'true')
            self.assertEqual(play.get(android+'foregroundServiceType'), 'specialUse')
            self.assertEqual(play.find('property').get(android+'name'), 'android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE')
            self.assertEqual(root.find('application/meta-data').get(android+'name'), 'org.godotengine.plugin.v2.RefiiInstall')
            # Every build also declares VIBRATE, from the export preset rather than this staging.
            info = ''.join("uses-permission: name='" + p.get(android+'name') + "'\n" for p in root.findall('uses-permission'))
            info += "uses-permission: name='android.permission.VIBRATE'\n"
            verifier = exporter.load_verifier()
            self.assertTrue(verifier.permission_policy(info, phone_installer=True)['passed'])
            self.assertFalse(verifier.permission_policy(info)['passed'])
            self.assertFalse(verifier.permission_policy(info+"uses-permission: name='android.permission.READ_CONTACTS'\n", phone_installer=True)['passed'])
            with self.assertRaises(ValueError): exporter.stage_install_service(project)
            staged = project/'android/build/src/main/java/com/godot/game/refii'
            self.assertEqual((staged/'RefiiInstallService.java').read_text(), 'public synthetic fixture')
            self.assertEqual((staged/'RefiiPlayService.java').read_text(), 'public synthetic fixture')

    def test_signed_apk_must_contain_the_exact_public_archive(self):
        payload = io.BytesIO()
        with zipfile.ZipFile(payload, 'w') as archive:
            archive.writestr('assets/phonecompiler/data.zip', b'public fixture')
        digest = hashlib.sha256(b'public fixture').hexdigest()
        with zipfile.ZipFile(payload) as archive:
            self.assertEqual(exporter.verify_phone_bundle_archive(archive, digest), digest)
            with self.assertRaises(ValueError): exporter.verify_phone_bundle_archive(archive, '0'*64)
        empty = io.BytesIO()
        with zipfile.ZipFile(empty, 'w'): pass
        with zipfile.ZipFile(empty) as archive:
            with self.assertRaises(ValueError): exporter.verify_phone_bundle_archive(archive, digest)

    def test_extraction_enabled_native_entries_remain_stored(self):
        payload = io.BytesIO()
        with zipfile.ZipFile(payload, 'w') as archive:
            for name in ('lib/arm64-v8a/libSystem.Native.so', 'assets/phonecompiler/data.zip'):
                entry = zipfile.ZipInfo(name)
                entry.compress_type = zipfile.ZIP_DEFLATED
                exporter.write_base_entry(archive, entry, b'fixture bytes')
        with zipfile.ZipFile(payload) as archive:
            self.assertEqual(archive.getinfo('lib/arm64-v8a/libSystem.Native.so').compress_type, zipfile.ZIP_STORED)
            self.assertEqual(archive.getinfo('assets/phonecompiler/data.zip').compress_type, zipfile.ZIP_DEFLATED)
            self.assertEqual(archive.read('lib/arm64-v8a/libSystem.Native.so'), b'fixture bytes')

    def test_only_android_scene_resources_and_extraction_change(self):
        project = 'run/main_scene="res://main.tscn"\nrun/main_scene.android="res://src/android/AndroidGame.tscn"\n'
        preset = ('[preset.0]\nname="Windows"\ninclude_filter=""\n[preset.0.options]\nfoo=true\n'
                  '[preset.3]\nname="Android ARM64"\ninclude_filter="*.txt"\n'
                  '[preset.3.options]\ngradle_build/compress_native_libraries=false\n')
        settings, selected = exporter.phone_compiler_settings(project, preset)
        self.assertIn('run/main_scene="res://main.tscn"', settings)
        self.assertIn('run/main_scene.android="res://main.tscn"', settings)
        self.assertTrue(selected.startswith(preset.split('[preset.3]')[0]))
        self.assertIn('include_filter="*.txt,phonecompiler/data.zip"', selected)
        self.assertIn('compress_native_libraries=true', selected)
        self.assertEqual(exporter.phone_compiler_settings(settings, selected), (settings, selected))
        with self.assertRaises(ValueError): exporter.phone_compiler_settings('', preset)
        with self.assertRaises(ValueError): exporter.phone_compiler_settings(project, '')

    def fixture(self, root, name='include/public.h', corrupt=False, api=31, native_digest=None):
        """Create a tiny ordinary public archive with complete byte identity."""
        bundle = root/'bundle'
        bundle.mkdir()
        project = root/'project'
        project.mkdir()
        native = root/'native.so'
        native.write_bytes(b'synthetic native fixture')
        native_manifest = root/'native-manifest.json'
        native_manifest.write_text('{}')
        digest = lambda value: hashlib.sha256(value).hexdigest()
        recipe = dict(Schema=1, Abi='arm64-v8a', Api=api, Ndk='28.2.13676358',
                      SdkPin='bc812c08ab2efd4ed8d7f306bd1911c703771732',
                      Files={name: '0'*64 if corrupt else digest(b'public header')},
                      NativeFiles={'native.so': native_digest or digest(native.read_bytes())})
        data = bundle/'data.zip'
        with zipfile.ZipFile(data, 'w') as archive:
            archive.writestr(name, b'public header')
            archive.writestr('bundle.json', json.dumps(recipe))
        (bundle/'bundle-proof.json').write_text(json.dumps(dict(schema=1,
            contains_game_files=False, private_module_excluded=True, data_sha256=digest(data.read_bytes()),
            native_manifest_sha256=digest(native_manifest.read_bytes()))))
        return bundle, project, native_manifest, native

    def test_complete_public_bundle_stages_exactly(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle, project, manifest, native = self.fixture(Path(temp))
            verifier = exporter.load_verifier()
            with patch.object(exporter, 'load_verifier', return_value=verifier), \
                 patch.object(verifier, 'native_inputs', return_value=({}, {'native.so':native})):
                proof = exporter.stage_phone_compiler(bundle, project, manifest)
            self.assertEqual((project/'phonecompiler/data.zip').read_bytes(), (bundle/'data.zip').read_bytes())
            self.assertFalse(proof['contains_game_files'])

    def test_private_paths_bad_hashes_pins_and_native_identity_never_stage(self):
        for options in (dict(name='default.xex'), dict(name='../escape.h'), dict(corrupt=True),
                        dict(api=33), dict(native_digest='0'*64)):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as temp:
                bundle, project, manifest, native = self.fixture(Path(temp), **options)
                verifier = exporter.load_verifier()
                with patch.object(exporter, 'load_verifier', return_value=verifier), \
                     patch.object(verifier, 'native_inputs', return_value=({}, {'native.so':native})):
                    with self.assertRaises(ValueError): exporter.stage_phone_compiler(bundle, project, manifest)
                self.assertFalse((project/'phonecompiler/data.zip').exists())


if __name__ == '__main__': unittest.main()
