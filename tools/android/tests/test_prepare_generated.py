"""Pure unit and complete preparation-lifecycle tests; no private game inputs."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("prepare_generated", Path(__file__).parents[1] / "prepare-generated.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class BarrierUnits(unittest.TestCase):
    def test_three_markers_crlf_and_unrelated_bytes(self):
        data = b'int f(){\r\n\t// sync \r\n\t// lwsync\t\r\n // eieio \r\nreturn 7;\r\n}\r\n'
        output, counts = MODULE.restore_barriers(data)
        self.assertEqual(output.count(MODULE.FENCE), 3)
        self.assertEqual(sum(value['inserted'] for value in counts.values()), 3)
        self.assertIn(b'\t// sync \r\n', output)
        self.assertIn(b'return 7;\r\n', output)
        self.assertNotIn(b'\n', output.replace(b'\r\n', b''))

    def test_corrected_source_is_byte_idempotent(self):
        once, _ = MODULE.restore_barriers(b'// lwsync \n')
        twice, counts = MODULE.restore_barriers(once)
        self.assertEqual(once, twice)
        self.assertEqual(counts['lwsync'], {'inserted': 0, 'existing': 1, 'guard_upgraded': 0})

    def test_old_guard_upgrades_without_duplicate_fence(self):
        data = b'// sync\n#if ' + MODULE.LEGACY_GUARD.encode() + b'\n\t' + MODULE.FENCE + b'\n#endif\n'
        output, counts = MODULE.restore_barriers(data)
        self.assertEqual(output.count(MODULE.FENCE), 1)
        self.assertIn(MODULE.ARM64_GUARD.encode(), output)
        self.assertEqual(counts['sync']['guard_upgraded'], 1)
        self.assertEqual(MODULE.restore_barriers(output)[0], output)

    def test_untouched_lookalikes_are_byte_identical(self):
        data = b'// synchronizer\n// note: sync\nconst char* p="// sync";\n'
        self.assertEqual(MODULE.restore_barriers(data)[0], data)

    def test_next_instruction_own_fence_is_unchanged(self):
        data = b'// lwsync\n// mfmsr r7\n' + MODULE.FENCE + b'\n'
        output, counts = MODULE.restore_barriers(data)
        self.assertEqual(output.count(MODULE.FENCE), 2)
        self.assertEqual(counts['lwsync']['inserted'], 1)

    def test_malformed_or_ambiguous_expansion_refused(self):
        cases = [b'// sync 1\n', b'// lwsync extra\n', b'// eieio;\n',
                 b'// sync\n' + MODULE.FENCE + b'\n',
                 b'// sync\n#if mystery\n' + MODULE.FENCE + b'\n#endif\n',
                 b'// sync\n#if ' + MODULE.ARM64_GUARD.encode() + b'\n' + MODULE.FENCE + b'\n']
        for data in cases:
            with self.subTest(data=data), self.assertRaises(ValueError):
                MODULE.restore_barriers(data)

    def test_mixed_legacy_corrected_and_zero_site_source(self):
        corrected = MODULE.restore_barriers(b'// sync\n')[0]
        output, counts = MODULE.restore_barriers(corrected + b'// lwsync\n')
        self.assertEqual(output.count(MODULE.FENCE), 2)
        self.assertEqual(output.count(MODULE.MARKER), 1)
        self.assertEqual(counts['sync']['existing'], 1)
        self.assertEqual(counts['lwsync']['inserted'], 1)
        self.assertEqual(MODULE.restore_barriers(b'void empty(){}\n')[0], b'void empty(){}\n')


class PreparationLifecycle(unittest.TestCase):
    def fixture(self, root):
        """Create a complete tiny generator output with private-style build metadata."""
        source = root / 'source'
        source.mkdir()
        (source / 'refii_pch.h').write_bytes(b'#pragma once\n#include <atomic>\n')
        (source / 'refii_recomp.0.cpp').write_bytes(b'void f(){\n// lwsync\n}\n')
        (source / 'refii_init.cpp').write_bytes(b'void init(){}\n')
        (source / 'sources.cmake').write_bytes(b'set(GENERATED_SOURCES ${CMAKE_CURRENT_LIST_DIR}/refii_init.cpp)\n')
        (source / 'codegen.stamp').write_bytes(b'opaque ignored metadata')
        return source

    def test_full_publish_reuse_headers_and_originals_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            before = {path.name: path.read_bytes() for path in source.iterdir()}
            manifest = root / 'report.json'
            first = MODULE.prepare(source, root / 'output', manifest)
            prepared = Path(first['prepared_directory'])
            self.assertEqual(first['counts']['lwsync']['inserted'], 1)
            self.assertEqual((prepared / 'refii_pch.h').read_bytes(), before['refii_pch.h'])
            self.assertEqual((prepared / 'refii_init.cpp').read_bytes(), before['refii_init.cpp'])
            self.assertFalse((prepared / 'codegen.stamp').exists())
            self.assertEqual({path.name: path.read_bytes() for path in source.iterdir()}, before)
            second = MODULE.prepare(source, root / 'output', manifest)
            self.assertEqual(first, second)
            self.assertEqual(len([path for path in (root / 'output').iterdir() if not path.name.startswith('.')]), 1)
            self.assertEqual(json.loads(manifest.read_text()), first)

    def test_changed_input_publishes_new_stage_and_preserves_previous(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            first = MODULE.prepare(source, root / 'output', root / 'report.json')
            previous = Path(first['prepared_directory']) / 'refii_recomp.0.cpp'
            saved = previous.read_bytes()
            (source / 'refii_recomp.0.cpp').write_bytes(b'// sync\n')
            second = MODULE.prepare(source, root / 'output', root / 'report.json')
            self.assertNotEqual(first['prepared_directory'], second['prepared_directory'])
            self.assertEqual(previous.read_bytes(), saved)

    def test_tampered_cached_output_is_refused_without_repair(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            first = MODULE.prepare(source, root / 'output', root / 'report.json')
            cached = Path(first['prepared_directory']) / 'refii_pch.h'
            cached.write_bytes(b'tampered')
            with self.assertRaises(ValueError):
                MODULE.prepare(source, root / 'output', root / 'report.json')
            self.assertEqual(cached.read_bytes(), b'tampered')

    def test_late_malformed_source_never_publishes_or_changes_inputs(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            (source / 'z.cpp').write_bytes(b'// eieio broken\n')
            before = {path.name: path.read_bytes() for path in source.iterdir()}
            with self.assertRaises(ValueError):
                MODULE.prepare(source, root / 'output', root / 'report.json')
            self.assertFalse((root / 'output').exists())
            self.assertFalse((root / 'report.json').exists())
            self.assertEqual({path.name: path.read_bytes() for path in source.iterdir()}, before)

    def test_missing_atomic_flatness_and_output_in_source_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            with self.assertRaises(ValueError):
                MODULE.prepare(source, source / 'output', root / 'report.json')
            with self.assertRaises(ValueError):
                MODULE.prepare(source, root / 'output', source / 'report.json')
            (source / 'refii_pch.h').write_bytes(b'#pragma once\n')
            with self.assertRaises(ValueError):
                MODULE.prepare(source, root / 'output', root / 'report.json')
            (source / 'refii_pch.h').write_bytes(b'#include <atomic>\n')
            (source / 'nested').mkdir()
            with self.assertRaises(ValueError):
                MODULE.prepare(source, root / 'output', root / 'report.json')

    def test_new_generator_output_reuses_with_no_added_fences(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = self.fixture(root)
            corrected = MODULE.restore_barriers((source / 'refii_recomp.0.cpp').read_bytes())[0]
            (source / 'refii_recomp.0.cpp').write_bytes(corrected)
            result = MODULE.prepare(source, root / 'output', root / 'report.json')
            self.assertEqual(result['counts']['lwsync']['existing'], 1)
            self.assertEqual(result['counts']['lwsync']['inserted'], 0)
            self.assertEqual((Path(result['prepared_directory']) / 'refii_recomp.0.cpp').read_bytes(), corrected)


if __name__ == '__main__':
    unittest.main(verbosity=2)
