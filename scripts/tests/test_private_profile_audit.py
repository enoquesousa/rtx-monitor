"""Regression checks for the offline profile audit; no GPU or compiler needed."""

import copy
import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "verify-private-profile.py"
SPEC = importlib.util.spec_from_file_location("private_profile_audit", SCRIPT)
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


class PrivateProfileAuditTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.snapshot = {
            "schema_version": 1, "snapshot_kind": "compiled_private_catalog", "profile_count": 1,
            "profile_id": "single-reviewed-test-profile", "profile_revision": 2,
            "gsp": {"state": "not_observed", "version": None},
            "operations": [{"operation": "thermal", "function_rva": "0x001e0bc0",
                            "value_width_bytes": 4, "minimum_interval_ms": 100}],
        }
        self.manifest = {
            "schema_version": 1,
            "hardware_scope": {"label": "User supplied label", "label_source": "user_statement",
                               "instance_count": 1, "pci_manufacturer_inference": False},
            "expected_snapshot": copy.deepcopy(self.snapshot),
            "policy_source_files": [self.file_entry("policy.c", b"compiled policy\n")],
            "evidence_sources": [self.file_entry("evidence.md", b"recorded evidence\n")],
            "fixture_sources": [self.file_entry("fixture.json", b'{"synthetic":true}\n')],
            "revision_history": [],
        }
        self.anchor()

    def file_entry(self, relative, data):
        path = self.root / relative
        path.write_bytes(data)
        return {"path": relative, "sha256_lf": audit.normalized_sha256(path), "provenance": "test-only synthetic input"}

    def anchor(self):
        self.manifest["revision_history"] = [{
            "revision": self.manifest["expected_snapshot"]["profile_revision"], "recorded_on": "2026-09-05",
            "change_summary": "Explicit synthetic test audit record.",
            "snapshot_sha256": audit.canonical_sha256(self.manifest["expected_snapshot"]),
            "policy_sources_sha256": audit.canonical_sha256(self.manifest["policy_source_files"]),
            "evidence_sources_sha256": audit.canonical_sha256(self.manifest["evidence_sources"]),
            "fixture_sources_sha256": audit.canonical_sha256(self.manifest["fixture_sources"]),
        }]

    def verify(self):
        return audit.verify(self.snapshot, self.manifest, self.root)

    def reject(self, pattern):
        with self.assertRaisesRegex(audit.AuditError, pattern):
            self.verify()

    def test_valid_record_and_normalized_line_endings(self):
        result = self.verify()
        self.assertEqual(result["status"], "verified")
        self.assertFalse(result["hardware_access"])
        (self.root / "policy.c").write_bytes(b"compiled policy\r\n")
        self.assertEqual(self.verify(), result)

    def test_snapshot_missing_extra_or_type_changed_field(self):
        original = copy.deepcopy(self.snapshot)
        for mutation in ("missing", "extra", "boolean"):
            with self.subTest(mutation=mutation):
                self.snapshot = copy.deepcopy(original)
                if mutation == "missing":
                    del self.snapshot["operations"]
                elif mutation == "extra":
                    self.snapshot["additional_profile"] = "unreviewed"
                else:
                    self.snapshot["profile_count"] = True
                self.reject("differs from reviewed pins")

    def test_changed_profile_rva_width_rate_or_revision(self):
        for path, value in (
            (("profile_id",), "another-profile"), (("profile_count",), 2),
            (("profile_revision",), 3), (("operations", 0, "function_rva"), "0x001e0bc1"),
            (("operations", 0, "value_width_bytes"), 8), (("operations", 0, "minimum_interval_ms"), 1),
        ):
            with self.subTest(path=path):
                self.snapshot = copy.deepcopy(self.manifest["expected_snapshot"])
                target = self.snapshot
                for segment in path[:-1]:
                    target = target[segment]
                target[path[-1]] = value
                self.reject("differs from reviewed pins")

    def test_source_evidence_and_fixture_tamper(self):
        for relative in ("policy.c", "evidence.md", "fixture.json"):
            with self.subTest(relative=relative):
                path = self.root / relative
                before = path.read_bytes()
                path.write_bytes(before + b"tampered")
                self.reject("hash mismatch")
                path.write_bytes(before)

    def test_updated_file_hash_requires_revision_record_update(self):
        path = self.root / "policy.c"
        path.write_bytes(b"different compiled policy\n")
        self.manifest["policy_source_files"][0]["sha256_lf"] = audit.normalized_sha256(path)
        self.reject("does not anchor policy_sources_sha256")

    def test_updated_snapshot_requires_revision_record_update(self):
        self.snapshot["operations"][0]["value_width_bytes"] = 8
        self.manifest["expected_snapshot"] = copy.deepcopy(self.snapshot)
        self.reject("does not anchor snapshot_sha256")

    def test_new_catalog_revision_requires_matching_record(self):
        self.snapshot["profile_revision"] = 3
        self.manifest["expected_snapshot"]["profile_revision"] = 3
        self.reject("active revision record is missing")

    def test_duplicate_or_missing_revision_record(self):
        original = copy.deepcopy(self.manifest["revision_history"])
        self.manifest["revision_history"] = []
        self.reject("missing revision history")
        self.manifest["revision_history"] = original + original
        self.reject("strictly increasing")

    def test_missing_file_or_malformed_hash(self):
        (self.root / "fixture.json").unlink()
        self.reject("missing or external fixture file")
        self.manifest["fixture_sources"][0]["sha256_lf"] = "invalid"
        self.anchor()
        self.reject("invalid SHA-256")

    def test_unsafe_or_duplicate_manifest_path(self):
        original = copy.deepcopy(self.manifest["fixture_sources"])
        for relative in ("../outside.json", "C:/outside.json", "/outside.json", "a\\b.json", "./fixture.json"):
            with self.subTest(path=relative):
                self.manifest["fixture_sources"] = copy.deepcopy(original)
                self.manifest["fixture_sources"][0]["path"] = relative
                self.anchor()
                self.reject("path")
        self.manifest["fixture_sources"] = original + original
        self.anchor()
        self.reject("duplicate fixture path")

    def test_gsp_observation_cannot_be_fabricated(self):
        self.snapshot["gsp"] = {"state": "validated", "version": "unobserved-version"}
        self.manifest["expected_snapshot"] = copy.deepcopy(self.snapshot)
        self.anchor()
        self.reject("does not validate a GSP version")

    def test_manufacturer_label_is_not_a_pci_inference(self):
        self.manifest["hardware_scope"]["pci_manufacturer_inference"] = True
        self.reject("single user-identified board")

    def test_duplicate_json_properties_and_non_finite_numbers(self):
        path = self.root / "duplicate.json"
        for content in ('{"profile_count":1,"profile_count":2}', '{"nested":{"x":1,"x":2}}', '{"value":NaN}'):
            with self.subTest(content=content):
                path.write_text(content, encoding="utf-8")
                with self.assertRaises(audit.AuditError):
                    audit.load_json(path)

    def test_cli_returns_nonzero_for_audit_failure(self):
        path = self.root / "snapshot.json"
        manifest_path = self.root / "manifest.json"
        path.write_text(json.dumps(self.snapshot), encoding="utf-8")
        manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        output = io.StringIO()
        errors = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            self.assertEqual(audit.main(["--snapshot", str(path), "--manifest", str(manifest_path), "--root", str(self.root)]), 0)
            self.snapshot["profile_revision"] = 9
            path.write_text(json.dumps(self.snapshot), encoding="utf-8")
            self.assertEqual(audit.main(["--snapshot", str(path), "--manifest", str(manifest_path), "--root", str(self.root)]), 1)
        self.assertEqual(json.loads(output.getvalue())["status"], "verified")
        self.assertIn("differs from reviewed pins", errors.getvalue())


if __name__ == "__main__":
    unittest.main()
