"""Small synthetic boundary tests; no SDK, game files, Godot or device needed."""
import importlib.util
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from android_common import export_errors, inspect_elf, private_resource, safe_relative

spec = importlib.util.spec_from_file_location("refii_export_apk", Path(__file__).resolve().parents[1] / "export-apk.py")
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)
verifier = exporter.load_verifier()


def elf_fixture(alignment=16384, relro=False):
    """A minimal ELF64 DSO with real dynamic names and an Android API note."""
    data = bytearray(512)
    data[:16] = b"\x7fELF\x02\x01\x01" + bytes(9)
    count = 4 if relro else 3
    struct.pack_into("<HHIQQQIHHHHHH", data, 16, 3, 183, 1, 0, 64, 0, 0, 64, 56, count, 64, 0, 0)
    struct.pack_into("<IIQQQQQQ", data, 64, 1, 6 if relro else 5, 0, 0, 0, 512, 512, alignment)
    struct.pack_into("<IIQQQQQQ", data, 120, 2, 6, 288, 288, 288, 64, 64, 8)
    struct.pack_into("<IIQQQQQQ", data, 176, 4, 4, 400, 400, 400, 24, 24, 4)
    if relro:
        struct.pack_into("<IIQQQQQQ", data, 232, 0x6474E552, 4, 352, 352, 352, 16, 16, 1)
    for i, (tag, value) in enumerate(((5, 352), (14, 1), (1, 15), (0, 0))):
        struct.pack_into("<qQ", data, 288 + i * 16, tag, value)
    strings = b"\0libexample.so\0libc.so\0"
    data[352:352 + len(strings)] = strings
    struct.pack_into("<III", data, 400, 8, 4, 1)
    data[412:420] = b"Android\0"
    struct.pack_into("<I", data, 420, 31)
    return bytes(data)


class PrivacyAndStagingTests(unittest.TestCase):
    def test_private_sources_and_apk_entries_rejected(self):
        for name in ("assets/save/profile/mainsave.bin", "assets/assets/save/profile/save.dat",
                     "game/data/main.bnk", "assets/default.xex", "lib/arm64-v8a/librefii_generated.so",
                     "assets/tests/test.cs", "assets/android/build/AndroidManifest.xml", "android-runtime.json"):
            with self.subTest(name=name):
                self.assertTrue(private_resource(name))

    def test_authored_and_managed_resources_retained(self):
        for name in ("assets/fonts/readable.ttf", "assets/shaders/standard.gdshader",
                     "assets/.godot/mono/publish/arm64/refii-launcher.dll", "src/android/AndroidGame.tscn"):
            self.assertFalse(private_resource(name), name)

    def test_relative_paths_cannot_escape(self):
        for name in ("../save", "a/../save", "/absolute", "C:/private", "a/./b", "..\\save", ""):
            with self.subTest(name=name), self.assertRaises(ValueError):
                safe_relative(name)
        self.assertEqual(str(safe_relative("res/shader.gdshader")), "res/shader.gdshader")

    def test_android_source_retained_but_template_and_tests_not_staged(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            names = ["Godot/project.godot", "Godot/src/android/AndroidGame.tscn", "Godot/android/build/build.gradle",
                     "Godot/tests/Test.cs", "Godot/assets/save/profile.dat", "Godot/shaders/test.gdshader"]
            for name in names:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("source", encoding="utf-8")
            with patch.object(exporter.subprocess, "check_output", return_value=("\0".join(names) + "\0").encode()):
                found = {p.relative_to(root).as_posix() for p in exporter.source_files(root)}
            self.assertEqual(found, {names[0], names[1], names[-1]})

    def test_source_ancestor_links_and_junctions_rejected_before_copy(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "Godot/src/linked/secret.cs"
            path.parent.mkdir(parents=True)
            path.write_text("source", encoding="utf-8")
            for attribute in ("is_symlink", "is_junction"):
                if not hasattr(Path, attribute):
                    continue
                with patch.object(Path, attribute, lambda p: p.name == "linked"):
                    with self.assertRaises(ValueError):
                        exporter.validate_source_path(root, path)

    def test_resolved_source_escape_and_in_checkout_caches_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "Godot/src/file.cs"
            original = Path.resolve
            with patch.object(Path, "resolve", lambda p, *a, **k: root / "outside.cs" if p == path else original(p, *a, **k)):
                with self.assertRaises(ValueError):
                    exporter.validate_source_path(root, path)
            with self.assertRaises(ValueError):
                exporter.external_directory(root, root / "cache", "Cache root")
            self.assertEqual(exporter.external_directory(root, root.parent / "external-cache", "Cache root"), root.parent / "external-cache")

    def test_junction_reparse_tag_fallback_on_older_python(self):
        with patch.object(Path, "is_junction", None, create=True), patch.object(Path, "lstat",
                return_value=SimpleNamespace(st_reparse_tag=0xA0000003)):
            self.assertTrue(exporter.source_junction(Path("junction")))
        with patch.object(Path, "is_junction", None, create=True), patch.object(Path, "lstat",
                return_value=SimpleNamespace(st_reparse_tag=0)):
            self.assertFalse(exporter.source_junction(Path("ordinary")))


class ElfAndExportTests(unittest.TestCase):
    def test_manifest_minimum_label_current_and_legacy(self):
        for label in ("minSdkVersion", "sdkVersion"):
            self.assertEqual(verifier.parse_badging("package: name='org.example.test'\n" + label + ":'33'\ntargetSdkVersion:'36'\n"),
                             ("org.example.test", 33, 36))
        with self.assertRaises(ValueError):
            verifier.parse_badging("package: name='org.example.test'")

    def test_actual_dynamic_names_and_api_note(self):
        result = inspect_elf(elf_fixture())
        self.assertEqual(result["soname"], "libexample.so")
        self.assertEqual(result["needed"], ["libc.so"])
        self.assertEqual(result["android_api_notes"], [31])
        self.assertTrue(result["load_alignment_16k"])

    def test_four_kib_load_and_unsafe_rounded_relro_detected(self):
        self.assertFalse(inspect_elf(elf_fixture(4096))["load_alignment_16k"])
        self.assertFalse(inspect_elf(elf_fixture(relro=True))["relro_padding_safe_16k"])

    def test_only_exact_teardown_error_whitelisted(self):
        harmless = 'ERROR: EditorSettings not instantiated yet when getting setting "export/android/shutdown_adb_on_exit".'
        self.assertEqual(export_errors(harmless), [])
        self.assertEqual(len(export_errors(harmless + "\nERROR: Can't write APK")), 1)
        self.assertEqual(len(export_errors("ERROR: EditorSettings failed\nBuild FAILED\nfile.cs: error CS1234: bad")), 3)

    def test_staged_preset_only_android_options_change(self):
        source = '[preset.0]\nname="Windows"\n[preset.0.options]\npackage/unique_name="desktop"\n[preset.4]\nname="Android ARM64"\n[preset.4.options]\npackage/unique_name="org.refii.android"\ngradle_build/min_sdk="33"\ngradle_build/target_sdk="36"\n'
        result = exporter.set_preset(source, "org.example.test", 31, 36)
        self.assertIn('package/unique_name="desktop"', result)
        self.assertIn('package/unique_name="org.example.test"', result)
        self.assertIn('gradle_build/min_sdk="31"', result)

    def test_android_app_label_uses_the_shared_game_name(self):
        root = Path(__file__).resolve().parents[3]
        presets = (root / "Godot/export_presets.cfg").read_text(encoding="utf-8")
        android = presets.split('name="Android ARM64"', 1)[1]
        options = android.split(".options]", 1)[1].split("\n[", 1)[0]
        self.assertIn('package/name="Fable: Return to Albion"', options.splitlines())

    def test_debug_network_changes_only_android_permission_and_requires_opt_in(self):
        source = ('[preset.0]\nname="Windows"\n[preset.0.options]\npermissions/internet=true\n'
                  '[preset.4]\nname="Android ARM64"\n[preset.4.options]\n'
                  'package/unique_name="org.refii.android"\ngradle_build/min_sdk="33"\n'
                  'gradle_build/target_sdk="36"\npermissions/internet=false\npermissions/vibrate=false\n')
        self.assertEqual(exporter.set_preset(source, "org.refii.android", 33, 36), source)
        selected = exporter.set_preset(source, "org.refii.android", 33, 36, True)
        self.assertEqual(selected, source.replace('permissions/internet=false', 'permissions/internet=true'))
        self.assertEqual(exporter.set_preset(selected, "org.refii.android", 33, 36, True), selected)
        with self.assertRaisesRegex(ValueError, "opt-in"):
            exporter.set_preset(selected, "org.refii.android", 33, 36)
        absent = source.replace('permissions/internet=false\n', '')
        self.assertEqual(exporter.set_preset(absent, "org.refii.android", 33, 36), absent)
        self.assertIn('permissions/internet=true', exporter.set_preset(absent, "org.refii.android", 33, 36, True))
        for bad in ('permissions/internet=false\npermissions/internet=true', 'permissions/internet="true"'):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                exporter.set_preset(source.replace('permissions/internet=false', bad), "org.refii.android", 33, 36, True)

    def test_netplay_enables_exactly_the_co_op_permissions(self):
        source = ('[preset.4]\nname="Android ARM64"\n[preset.4.options]\n'
                  'package/unique_name="org.refii.android"\ngradle_build/min_sdk="33"\n'
                  'gradle_build/target_sdk="36"\npermissions/internet=false\npermissions/vibrate=false\n')
        selected = exporter.set_preset(source, "org.refii.android", 33, 36, netplay=True)
        for name in exporter.NETPLAY_PERMISSIONS:
            with self.subTest(name=name):
                self.assertEqual(selected.count("permissions/" + name + "="), 1)
                self.assertIn("permissions/" + name + "=true", selected)
        self.assertIn("permissions/vibrate=false", selected)
        self.assertEqual(exporter.set_preset(selected, "org.refii.android", 33, 36, netplay=True), selected)
        with self.assertRaisesRegex(ValueError, "opt-in"):
            exporter.set_preset(selected, "org.refii.android", 33, 36)

    def test_nativeaot_sets_only_the_bionic_option_and_requires_opt_in(self):
        source = ('[preset.0]\nname="Windows"\n[preset.0.options]\npackage/unique_name="desktop"\n'
                  '[preset.4]\nname="Android ARM64"\n[preset.4.options]\n'
                  'package/unique_name="org.refii.android"\ngradle_build/min_sdk="33"\n'
                  'gradle_build/target_sdk="36"\ndotnet/embed_build_outputs=true\n')
        self.assertNotIn(exporter.NATIVEAOT_OPTION, exporter.set_preset(source, "org.refii.android", 33, 36))
        selected = exporter.set_preset(source, "org.refii.android", 33, 36, nativeaot=True)
        self.assertEqual(selected, source.rstrip() + "\n" + exporter.NATIVEAOT_OPTION + "=true\n")
        self.assertEqual(exporter.set_preset(selected, "org.refii.android", 33, 36, nativeaot=True), selected)
        explicit_off = source.rstrip() + "\n" + exporter.NATIVEAOT_OPTION + "=false\n"
        self.assertEqual(exporter.set_preset(explicit_off, "org.refii.android", 33, 36), explicit_off)
        self.assertEqual(exporter.set_preset(explicit_off, "org.refii.android", 33, 36, nativeaot=True), selected)
        with self.assertRaisesRegex(ValueError, "opt-in"):
            exporter.set_preset(selected, "org.refii.android", 33, 36)
        for bad in (selected + exporter.NATIVEAOT_OPTION + "=true\n", explicit_off.replace("=false", '="true"')):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                exporter.set_preset(bad, "org.refii.android", 33, 36, nativeaot=True)

    def test_managed_layout_matches_the_selected_runtime(self):
        mono = [verifier.MANAGED_ENTRY, "lib/arm64-v8a/libmonosgen-2.0.so", "lib/arm64-v8a/libgodot_android.so"]
        aot = [verifier.NATIVEAOT_ENTRY, "lib/arm64-v8a/libgodot_android.so",
               "assets/.godot/mono/publish/arm64/.dotnet-publish-manifest"]
        self.assertEqual(verifier.managed_layout(mono, False), [])
        self.assertEqual(verifier.managed_layout(aot, False), ["Missing embedded ARM64 managed assembly"])
        self.assertEqual(verifier.managed_layout(aot, True), [])
        failures = verifier.managed_layout(mono, True)
        self.assertEqual(len(failures), 3)
        self.assertIn("libmonosgen-2.0.so", failures[1])
        self.assertIn("refii-launcher.dll", failures[2])

    def test_netplay_permission_policy_is_exact(self):
        header = ("package: name='org.refii.android'\nsdkVersion:'31'\ntargetSdkVersion:'36'\n"
                  "uses-permission: name='android.permission.VIBRATE'\n")
        rows = "".join(f"uses-permission: name='{name}'\n" for name in sorted(verifier.NETPLAY_PERMISSIONS))
        self.assertTrue(verifier.permission_policy(header + rows, netplay=True)["passed"])
        self.assertFalse(verifier.permission_policy(header + rows)["passed"])
        partial = rows.replace("uses-permission: name='android.permission.CHANGE_WIFI_MULTICAST_STATE'\n", "")
        missing = verifier.permission_policy(header + partial, netplay=True)
        self.assertFalse(missing["passed"])
        self.assertEqual(missing["missing"], ["android.permission.CHANGE_WIFI_MULTICAST_STATE"])

    def test_actual_manifest_permission_policy_exact_default_and_opt_in(self):
        bare = "package: name='org.refii.android'\nsdkVersion:'31'\ntargetSdkVersion:'36'\n"
        header = bare + "uses-permission: name='android.permission.VIBRATE'\n"
        internet = "uses-permission: name='android.permission.INTERNET'\n"
        self.assertTrue(verifier.permission_policy(header)["passed"])
        # Every build vibrates the phone for the game's rumble: a manifest without VIBRATE fails.
        self.assertEqual(verifier.permission_policy(bare)["missing"], ["android.permission.VIBRATE"])
        self.assertFalse(verifier.permission_policy(header, True)["passed"])
        enabled = verifier.permission_policy(header + internet, True)
        self.assertTrue(enabled["passed"])
        self.assertEqual(enabled["declared"], ["android.permission.INTERNET", "android.permission.VIBRATE"])
        self.assertFalse(verifier.permission_policy(header + internet)["passed"])
        for prefix in ('uses-permission', 'uses-permission-sdk-23', 'uses-implied-permission'):
            row = header + internet + prefix + ": name='android.permission.READ_CONTACTS'\n"
            with self.subTest(prefix=prefix):
                rejected = verifier.permission_policy(row, True)
                self.assertFalse(rejected["passed"])
                self.assertEqual(rejected["unexpected"], ["android.permission.READ_CONTACTS"])
        for malformed in (internet + internet, "uses-permission: name=android.permission.INTERNET\n",
                          "uses-permission-unknown: name='android.permission.INTERNET'\n"):
            with self.subTest(malformed=malformed), self.assertRaises(ValueError):
                verifier.permission_policy(header + malformed, True)

    def test_android_import_representation_preserves_hdr_and_main_scene(self):
        source = '[application]\nrun/main_scene="res://main.tscn"\n[editor_plugins]\nenabled=PackedStringArray("res://addons/bnk_mount/plugin.cfg")\n[rendering]\nviewport/hdr_2d=true\n'
        result = exporter.android_export_settings(source)
        self.assertIn('textures/vram_compression/import_etc2_astc=true', result)
        self.assertIn('viewport/hdr_2d=true', result)
        self.assertIn('run/main_scene="res://main.tscn"', result)
        self.assertIn('enabled=PackedStringArray()', result)
        self.assertEqual(exporter.android_export_settings(result), result)

    def test_startup_splash_is_the_seal_on_black_in_application(self):
        source = '[application]\nrun/main_scene="res://main.tscn"\n\n[rendering]\nviewport/hdr_2d=true\n'
        result = exporter.startup_splash_settings(source)
        application = result.split("[rendering]")[0]
        self.assertIn('boot_splash/image="' + exporter.STARTUP_SPLASH + '"', application)
        self.assertIn("boot_splash/bg_color=Color(0, 0, 0, 1)", application)
        self.assertIn('run/main_scene="res://main.tscn"', application)
        self.assertEqual(exporter.startup_splash_settings(result), result)
        replaced = exporter.startup_splash_settings('[application]\nboot_splash/image="res://other.png"\n')
        self.assertEqual(replaced.count("boot_splash/image="), 1)
        self.assertIn(exporter.STARTUP_SPLASH, replaced)
        with self.assertRaises(ValueError):
            exporter.startup_splash_settings('[rendering]\nviewport/hdr_2d=true\n')
        with self.assertRaises(ValueError):
            exporter.startup_splash_settings('[application]\n' + 'boot_splash/image="a.png"\n' * 2)


class RenderingConfigurationTests(unittest.TestCase):
    def test_renderer_selection_changes_only_android_setting(self):
        source = ('[application]\nrun/main_scene="res://main.tscn"\n[rendering]\n'
                  'renderer/rendering_method="forward_plus"\n'
                  'renderer/rendering_method.android="forward_plus"\n'
                  'rendering_device/driver.windows="d3d12"\nviewport/hdr_2d=true\n')
        for method in ("forward_plus", "mobile"):
            with self.subTest(method=method):
                result = exporter.android_export_settings(source, method)
                self.assertIn('renderer/rendering_method.android="' + method + '"', result)
                self.assertIn('renderer/rendering_method="forward_plus"', result)
                self.assertIn('rendering_device/driver.windows="d3d12"', result)
                self.assertIn('viewport/hdr_2d=true', result)
                self.assertIn('run/main_scene="res://main.tscn"', result)
                self.assertEqual(exporter.android_export_settings(result, method), result)
        self.assertEqual(exporter.android_export_settings(source),
                         exporter.android_export_settings(source, "forward_plus"))

    def test_renderer_setting_added_to_missing_rendering_section(self):
        result = exporter.android_export_settings('[application]\nconfig/name="test"\n', "mobile")
        self.assertEqual(result.count("[rendering]"), 1)
        self.assertEqual(result.count('renderer/rendering_method.android="mobile"'), 1)
        self.assertIn('textures/vram_compression/import_etc2_astc=true', result)

    def test_duplicate_or_unsupported_renderer_rejected(self):
        source = '[rendering]\n' + 'renderer/rendering_method.android="mobile"\n' * 2
        with self.assertRaises(ValueError):
            exporter.android_export_settings(source, "mobile")
        with self.assertRaises(ValueError):
            exporter.android_export_settings("", "gl_compatibility")
        with self.assertRaises(ValueError):
            exporter.configure_launch_arguments(struct.pack("<I", 0), "gl_compatibility")

    def test_android_launch_format_uses_utf8_byte_lengths(self):
        values = ["--path", "./private-free", "caf\u00e9", ""]
        fixture = (b'\x04\0\0\0\x06\0\0\0--path\x0e\0\0\0./private-free'
                   b'\x05\0\0\0caf\xc3\xa9\0\0\0\0')
        self.assertEqual(exporter.decode_launch_arguments(fixture), values)
        self.assertEqual(exporter.encode_launch_arguments(values), fixture)

    def test_corrupt_android_launch_arguments_rejected(self):
        malformed = [b"", b"\0\0\0", struct.pack("<I", 0) + b"x",
                     struct.pack("<I", 0xffffffff), struct.pack("<II", 1, 10) + b"short",
                     struct.pack("<II", 1, 1) + b"\xff", struct.pack("<II", 1, 1) + b"\0",
                     struct.pack("<III", 2, 4, 0)]
        for data in malformed:
            with self.subTest(data=data), self.assertRaises(ValueError):
                exporter.decode_launch_arguments(data)
        with self.assertRaises(ValueError):
            exporter.encode_launch_arguments(["unsafe\0suffix"])

    def test_forward_default_preserves_existing_launch_bytes(self):
        data = exporter.encode_launch_arguments(["--xr-mode", "off", "--verbose", "--path", "."])
        self.assertEqual(exporter.configure_launch_arguments(data, "forward_plus"), data)

    def test_mobile_launch_override_preserves_other_arguments_and_is_idempotent(self):
        values = ["--xr-mode", "off", "--path", ".", "--rendering-driver", "vulkan"]
        data = exporter.encode_launch_arguments(values)
        selected = exporter.configure_launch_arguments(data, "mobile")
        self.assertEqual(exporter.decode_launch_arguments(selected),
                         values + ["--rendering-method", "mobile"])
        self.assertEqual(exporter.configure_launch_arguments(selected, "mobile"), selected)
        equal_syntax = exporter.encode_launch_arguments(values + ["--rendering-method=mobile"])
        self.assertEqual(exporter.configure_launch_arguments(equal_syntax, "mobile"), equal_syntax)

    def test_ambiguous_or_conflicting_existing_renderer_override_rejected(self):
        for values in (["--rendering-method"], ["--rendering-method", "--verbose"],
                       ["--rendering-method=forward_plus"],
                       ["--rendering-method", "mobile", "--rendering-method=mobile"],
                       ["--rendering-method="]):
            with self.subTest(values=values), self.assertRaises(ValueError):
                exporter.configure_launch_arguments(exporter.encode_launch_arguments(values), "mobile")
        with self.assertRaises(ValueError):
            exporter.configure_launch_arguments(exporter.encode_launch_arguments(
                ["--rendering-method", "mobile"]), "forward_plus")

    def test_gpu_profile_opt_in_preserves_other_arguments_and_is_idempotent(self):
        values = ["--xr-mode", "off", "--path", "caf\u00e9", "--verbose"]
        for method in ("forward_plus", "mobile"):
            with self.subTest(method=method):
                data = exporter.encode_launch_arguments(values)
                selected = exporter.configure_launch_arguments(data, method, True)
                additions = [] if method == "forward_plus" else ["--rendering-method", "mobile"]
                self.assertEqual(exporter.decode_launch_arguments(selected), values + additions + ["--gpu-profile"])
                self.assertEqual(exporter.configure_launch_arguments(selected, method, True), selected)

    def test_gpu_profile_is_never_implicitly_enabled(self):
        for method in ("forward_plus", "mobile"):
            with self.subTest(method=method):
                data = exporter.encode_launch_arguments(["--path", "."])
                self.assertNotIn("--gpu-profile", exporter.decode_launch_arguments(
                    exporter.configure_launch_arguments(data, method)))
                with self.assertRaisesRegex(ValueError, "opt-in"):
                    exporter.configure_launch_arguments(
                        exporter.encode_launch_arguments(["--gpu-profile"]), method)

    def test_gpu_profile_duplicate_and_equals_syntax_rejected(self):
        for values in (["--gpu-profile", "--gpu-profile"], ["--gpu-profile=true"],
                       ["--gpu-profile=false"], ["--gpu-profile="],
                       ["--gpu-profile", "--gpu-profile=true"]):
            for enabled in (False, True):
                with self.subTest(values=values, enabled=enabled), self.assertRaises(ValueError):
                    exporter.configure_launch_arguments(exporter.encode_launch_arguments(values),
                                                        "forward_plus", enabled)

    def test_engine_flags_precede_user_separator_without_consuming_user_arguments(self):
        values = ["--path", ".", "--", "--gpu-profile", "--rendering-method=forward_plus", "game-arg"]
        selected = exporter.configure_launch_arguments(exporter.encode_launch_arguments(values), "mobile", True)
        self.assertEqual(exporter.decode_launch_arguments(selected),
                         values[:2] + ["--rendering-method", "mobile", "--gpu-profile"] + values[2:])
        self.assertEqual(exporter.configure_launch_arguments(selected, "mobile", True), selected)
        self.assertEqual(exporter.configure_launch_arguments(exporter.encode_launch_arguments(values),
                                                            "forward_plus"), exporter.encode_launch_arguments(values))


class BundledModsTests(unittest.TestCase):
    """Fable2/mods-src ships in the APK as one source zip the Android launcher unpacks (AndroidBundledMods) and ModBundled installs."""

    def stage(self, root, names):
        for name in names:
            target = root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("source " + name, encoding="utf-8")
        project = root / "project"
        project.mkdir(exist_ok=True)
        with patch.object(exporter.subprocess, "check_output", return_value=("\0".join(names) + "\0").encode()):
            return exporter.stage_bundled_mods(root, project), project / exporter.BUNDLED_MODS

    def test_every_mod_file_and_the_manifest_are_zipped(self):
        names = ["Fable2/mods-src/hotbar/mod.toml", "Fable2/mods-src/hotbar/godot/hotbar.gd", "Fable2/mods-src/quicksave/mod/init.lua"]
        with tempfile.TemporaryDirectory() as temporary:
            report, archive_path = self.stage(Path(temporary), names)
            with zipfile.ZipFile(archive_path) as archive:
                listed = ["hotbar/godot/hotbar.gd", "hotbar/mod.toml", "quicksave/mod/init.lua"]
                self.assertEqual(sorted(archive.namelist()), sorted(listed + ["manifest.txt"]))
                self.assertEqual(archive.read("manifest.txt").decode().splitlines(), listed)
                self.assertEqual(archive.read("hotbar/mod.toml").decode(), "source Fable2/mods-src/hotbar/mod.toml")
            self.assertEqual(report["mods"], ["hotbar", "quicksave"])
            self.assertEqual(report["files"], 3)

    def test_the_zip_is_a_function_of_the_sources(self):
        names = ["Fable2/mods-src/a/mod.toml", "Fable2/mods-src/a/mod/init.lua"]
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            self.assertEqual(self.stage(Path(first), names)[0]["sha256"], self.stage(Path(second), names)[0]["sha256"])

    def test_a_file_outside_a_mod_folder_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(ValueError):
                self.stage(Path(temporary), ["Fable2/mods-src/stray.txt"])

    def test_include_resource_changes_only_the_android_filter(self):
        preset = ('[preset.0]\nname="Linux"\ninclude_filter=""\n[preset.0.options]\n'
                  '[preset.3]\nname="Android ARM64"\ninclude_filter="phonecompiler/data.zip"\n[preset.3.options]\n')
        selected = exporter.include_resource(preset, exporter.BUNDLED_MODS)
        self.assertTrue(selected.startswith(preset.split("[preset.3]")[0]))
        self.assertIn('include_filter="phonecompiler/data.zip,bundled-mods.zip"', selected)
        self.assertEqual(exporter.include_resource(selected, exporter.BUNDLED_MODS), selected)
        with self.assertRaises(ValueError):
            exporter.include_resource(preset.split("[preset.3]")[0], exporter.BUNDLED_MODS)


if __name__ == "__main__":
    unittest.main()
