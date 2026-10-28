"""Private-import boundary/lifecycle tests using tiny synthetic files, without ADB."""
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from android_common import sha256
from test_export_tools import elf_fixture

spec = importlib.util.spec_from_file_location("refii_prepare_import", Path(__file__).resolve().parents[1] / "prepare-import.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


def module_fixture() -> bytes:
    """Use a real tiny ELF64 header/string table with the required private module SONAME."""
    data = bytearray(elf_fixture())
    strings = b"\0librefii_generated.so\0libc.so\0"
    data[352:352 + len(strings)] = strings
    struct.pack_into("<Q", data, 288 + 2 * 16 + 8, strings.index(b"libc.so"))
    return bytes(data)


class ImportBoundaryTests(unittest.TestCase):
    def test_python310_junction_reparse_tag_fallback(self):
        raw = Path("junction-input")
        with patch.object(Path, "is_junction", None, create=True), \
                patch.object(Path, "is_symlink", return_value=False), \
                patch.object(Path, "lstat", return_value=SimpleNamespace(st_reparse_tag=0xA0000003)), \
                patch.object(Path, "resolve") as resolve:
            with self.assertRaises(ValueError):
                prepare.ordinary_input(raw)
            resolve.assert_not_called()

    def test_raw_symlink_and_junction_are_rejected_before_resolve(self):
        raw = Path("ordinary-looking-input")
        for kind in ("symlink", "junction"):
            with self.subTest(kind=kind), patch.object(Path, "is_symlink", return_value=kind == "symlink"), \
                    patch.object(Path, "is_junction", return_value=kind == "junction", create=True), \
                    patch.object(Path, "resolve") as resolve:
                with self.assertRaises(ValueError):
                    prepare.ordinary_input(raw)
                resolve.assert_not_called()

    def test_ordinary_entry_resolves_only_after_link_checks(self):
        raw, resolved = Path("ordinary-input"), Path("resolved-input")
        with patch.object(Path, "is_symlink", return_value=False), \
                patch.object(Path, "is_junction", return_value=False, create=True), \
                patch.object(Path, "resolve", return_value=resolved) as resolve:
            self.assertEqual(prepare.ordinary_input(raw), resolved)
            resolve.assert_called_once()

    def test_nested_save_links_and_junctions_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            saves = root / "save"
            saves.mkdir()
            nested = saves / "linked-account"
            nested.mkdir()
            for kind in ("symlink", "junction"):
                with self.subTest(kind=kind), \
                        patch.object(Path, "is_symlink", lambda path: path == nested and kind == "symlink"), \
                        patch.object(Path, "is_junction", lambda path: path == nested and kind == "junction", create=True):
                    with self.assertRaises(ValueError):
                        prepare.validate_save_tree(saves, root / "output")

    def test_save_output_overlap_and_invalid_android_paths_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            saves = Path(directory)
            child = saves / "nested"
            child.mkdir()
            for output, source in ((saves, saves), (child, saves), (saves, child)):
                with self.subTest(output=output, source=source), self.assertRaises(ValueError):
                    prepare.validate_save_tree(source, output)
        for value in ("relative", "content://tree/game", "/game/../save", "/game/./save"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                prepare.physical_android_path(value)


class ImportLifecycleTests(unittest.TestCase):
    def test_copies_complete_synthetic_tree_and_preserves_originals(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            module = root / "input.so"
            module.write_bytes(module_fixture())
            saves = root / "source-save"
            leaf = saves / "account/title/profile/mainsave.bin"
            leaf.parent.mkdir(parents=True)
            leaf.write_bytes(b"synthetic-private-save")
            (saves / "account/header.bin").write_bytes(b"synthetic-header")
            before = {p.relative_to(root).as_posix(): sha256(p) for p in (module, leaf, saves / "account/header.bin")}
            output = root / "private-bundle"
            argv = ["prepare-import.py", "--output", str(output), "--module", str(module),
                    "--save-root", str(saves), "--device-user-root", "/data/user/0/example.app/files",
                    "--device-game-root", "/storage/app-owned/game"]
            with patch.object(sys, "argv", argv):
                self.assertEqual(prepare.main(), 0)
            config = json.loads((output / "android-runtime.json").read_text())
            self.assertEqual(config["schema"], 1)
            self.assertEqual(config["game_root"], "/storage/app-owned/game")
            self.assertEqual(config["save_import_root"], "/data/user/0/example.app/files/imported-save")
            self.assertTrue(config["skip_movies"])
            manifest = json.loads((output / "PRIVATE-import-manifest.json").read_text())
            self.assertTrue(manifest["private_game_derived"])
            for relative, digest in manifest["sha256"].items():
                self.assertEqual(sha256(output / relative), digest)
            for relative, digest in before.items():
                self.assertEqual(sha256(root / relative), digest)
            # An existing bundle is immutable, including when the original source later changes.
            leaf.write_bytes(b"new original progress")
            with patch.object(sys, "argv", argv), self.assertRaises(SystemExit):
                prepare.main()
            self.assertEqual((output / "imported-save/account/title/profile/mainsave.bin").read_bytes(),
                             b"synthetic-private-save")

    def test_invalid_save_source_creates_no_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            module = root / "input.so"
            module.write_bytes(module_fixture())
            output = root / "new-output"
            argv = ["prepare-import.py", "--output", str(output), "--module", str(module),
                    "--save-root", str(root / "missing-save"), "--device-user-root", "/private/user",
                    "--device-game-root", "/private/game"]
            with patch.object(sys, "argv", argv), self.assertRaises(ValueError):
                prepare.main()
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
