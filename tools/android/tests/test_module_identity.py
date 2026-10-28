"""Synthetic recorded-header/module identity checks; no game data or SDK needed."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from android_common import sha256

spec = importlib.util.spec_from_file_location("verify_identity", Path(__file__).resolve().parents[1] / "verify-apk.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class ModuleIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.module = self.root / "librefii_generated.so"
        self.module.write_bytes(b"synthetic module identity only; ELF validation is tested separately")
        self.path = self.root / "private-module-manifest.json"
        self.native = dict(abi="arm64-v8a", header_identity={
            "src\\vfs\\include\\rex\\thread\\mutex.h": "a" * 64,
            "C:/builder/rexglue-sdk/include/rex/ppc/context.h": "b" * 64})
        self.private = dict(private_game_derived=True, abi="arm64-v8a", api=31, sha256=sha256(self.module),
            header_identity={"src/vfs/include/rex/thread/mutex.h": "a" * 64,
                "/another/checkout/rexglue-sdk/include/rex/ppc/context.h": "b" * 64,
                "tools/android/payload_abi_guard.h": "c" * 64},
            object_audit=dict(no_raw_recursive_mutex_imports=True, shared_critical_region_bridge_imported=True))

    def check(self):
        self.path.write_text(json.dumps(self.private), encoding="utf-8")
        return verifier.module_identity(self.native, self.module, self.path, 33)

    def test_matching_recorded_headers_across_sdk_locations(self):
        result = self.check()
        self.assertTrue(result["checked"])
        self.assertEqual(result["matching_runtime_headers"], 2)
        self.assertEqual(result["additional_module_headers"], 1)
        self.assertTrue(result["object_audit_recorded"])

    def test_changed_or_missing_runtime_header_rejected(self):
        for change in ("different", "missing"):
            with self.subTest(change=change):
                original = dict(self.private["header_identity"])
                if change == "different":
                    self.private["header_identity"]["src/vfs/include/rex/thread/mutex.h"] = "d" * 64
                else:
                    del self.private["header_identity"]["src/vfs/include/rex/thread/mutex.h"]
                with self.assertRaises(ValueError):
                    self.check()
                self.private["header_identity"] = original

    def test_wrong_binary_hash_architecture_api_or_object_audit_rejected(self):
        for key, value in (("sha256", "f" * 64), ("abi", "x86_64"), ("api", 34),
            ("object_audit", dict(no_raw_recursive_mutex_imports=False, shared_critical_region_bridge_imported=True))):
            with self.subTest(key=key):
                original = self.private[key]
                self.private[key] = value
                with self.assertRaises(ValueError):
                    self.check()
                self.private[key] = original

    def test_binary_only_scope_explicit_and_manifest_requires_module(self):
        self.assertFalse(verifier.module_identity(self.native, self.module, None, 33)["checked"])
        with self.assertRaises(ValueError):
            verifier.module_identity(self.native, None, self.path, 33)

    def test_empty_or_ambiguous_recorded_headers_rejected(self):
        for headers in ({}, {"C:/a/rexglue-sdk/include/rex/ppc/context.h": "a" * 64,
                             "D:/b/rexglue-sdk/include/rex/ppc/context.h": "b" * 64}):
            with self.assertRaises(ValueError):
                verifier.header_identity(headers)


if __name__ == "__main__":
    unittest.main()
