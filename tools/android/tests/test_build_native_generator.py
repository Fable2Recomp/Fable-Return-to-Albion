"""Exercise generator/PCH compatibility without proprietary input or native compilation."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("native_generator_compat", Path(__file__).resolve().parents[1] / "build-native.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class GeneratorCompatibilityTests(unittest.TestCase):
    def check_lifecycle(self, existing_pch, legacy_weak=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = root / "source/versions/Fable2TU1"
            config.mkdir(parents=True)
            (config / "refii_config.toml").write_text("# synthetic public config\n")
            (config / "refii_manifest.toml").write_text('file_path = "old"\nout_directory_path = "old"\n')
            game = root / "game"
            game.mkdir()
            (game / "default.xex").write_bytes(b"synthetic input, not an executable")
            def generated_run(*args):
                generated = root / "out/private-codegen/generated"
                generated.mkdir()
                (generated / "refii_init.h").write_text("// exact synthetic declarations\n" + ("REX_WEAK_FUNC(example);\n" if legacy_weak else ""))
                if existing_pch:
                    (generated / "refii_pch.h").write_text("// existing PCH\n#include <atomic>\n")
            with patch.object(native, "run", side_effect=generated_run):
                output = native.generate_private(root / "source", game, root / "generator.exe", root / "out", "Fable2TU1", [])
            self.assertEqual((output / "refii_init.h").read_text(), "// exact synthetic declarations\n" + ("REX_WEAK_FUNC(example);\n" if legacy_weak else ""))
            self.assertEqual((game / "default.xex").read_bytes(), b"synthetic input, not an executable")
            self.assertEqual((root / "out/private-codegen/input/default.xex").read_bytes(), (game / "default.xex").read_bytes())
            compatibility = '#ifndef REX_WEAK_FUNC\n#define REX_WEAK_FUNC(name) __attribute__((weak, noinline)) extern "C" REX_FUNC(name)\n#endif\n' if legacy_weak else ""
            expected = "// existing PCH\n#include <atomic>\n" if existing_pch else '#pragma once\n#include <atomic>\n' + compatibility + '#include "refii_init.h"\n'
            self.assertEqual((output / "refii_pch.h").read_text(), expected)
            with self.assertRaises(ValueError):
                native.generate_private(root / "source", game, root / "generator.exe", root / "out", "Fable2TU1", [])

    def test_legacy_init_header_gets_guarded_pch_entry_without_changing_source(self):
        self.check_lifecycle(False)

    def test_existing_generator_pch_and_original_inputs_are_preserved(self):
        self.check_lifecycle(True)

    def test_legacy_weak_alias_declaration_keeps_generated_header_unchanged(self):
        self.check_lifecycle(False, legacy_weak=True)
