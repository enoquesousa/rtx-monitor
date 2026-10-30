#!/usr/bin/env python3
"""Verify the offline compiled catalog against a reviewed, source-pinned record.

This verifier never launches the snapshot producer, loads a driver, changes the
catalog, or accepts new pins. The manifest is an audit record, not runtime config.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path, PurePosixPath
from typing import Any

MAX_JSON_BYTES = 1024 * 1024
HEX_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
TOP_LEVEL_FIELDS = {
    "schema_version", "hardware_scope", "expected_snapshot", "policy_source_files",
    "evidence_sources", "fixture_sources", "revision_history",
}
RECORD_FIELDS = {
    "revision", "recorded_on", "change_summary", "snapshot_sha256",
    "policy_sources_sha256", "evidence_sources_sha256", "fixture_sources_sha256",
}


class AuditError(ValueError):
    """A reviewed pin, file, or revision record does not match."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AuditError(message)


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                      allow_nan=False).encode("utf-8")


def canonical_sha256(value: Any) -> str:
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def normalized_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for name, value in pairs:
        require(name not in result, f"duplicate JSON property: {name}")
        result[name] = value
    return result


def reject_nonfinite(value: str) -> Any:
    raise AuditError(f"non-finite JSON number: {value}")


def load_json(path: Path) -> dict[str, Any]:
    with path.open("rb") as stream:
        raw = stream.read(MAX_JSON_BYTES + 1)
    require(len(raw) <= MAX_JSON_BYTES, f"JSON exceeds 1 MiB: {path.name}")
    # A BOM from PowerShell redirection changes no JSON data. Artifact hashes
    # still cover the exact file bytes except the documented CRLF normalization.
    value = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object,
                       parse_constant=reject_nonfinite)
    require(isinstance(value, dict), f"JSON root must be an object: {path.name}")
    return value


def verify_files(root: Path, entries: Any, label: str, allow_empty: bool = False) -> None:
    require(isinstance(entries, list) and (allow_empty or len(entries) > 0), f"invalid {label} list")
    seen: set[str] = set()
    root = root.resolve()
    for entry in entries:
        require(isinstance(entry, dict), f"invalid {label} entry")
        require(set(entry) == {"path", "sha256_lf", "provenance"}, f"unexpected fields in {label} entry")
        relative = entry["path"]
        require(isinstance(relative, str) and relative != "", f"missing path in {label}")
        require("\\" not in relative and ":" not in relative, f"non-portable path: {relative}")
        parsed = PurePosixPath(relative)
        require(not parsed.is_absolute() and all(part not in (".", "..") for part in parsed.parts)
                and parsed.as_posix() == relative, f"unsafe path: {relative}")
        require(relative not in seen, f"duplicate {label} path: {relative}")
        seen.add(relative)
        require(isinstance(entry["sha256_lf"], str) and HEX_SHA256.fullmatch(entry["sha256_lf"]) is not None,
                f"invalid SHA-256: {relative}")
        require(isinstance(entry["provenance"], str) and entry["provenance"].strip() != "",
                f"missing provenance: {relative}")
        path = (root / relative).resolve()
        require(path.is_relative_to(root) and path.is_file(), f"missing or external {label} file: {relative}")
        require(normalized_sha256(path) == entry["sha256_lf"], f"{label} hash mismatch: {relative}")


def verify(snapshot: dict[str, Any], manifest: dict[str, Any], root: Path) -> dict[str, Any]:
    require(set(manifest) == TOP_LEVEL_FIELDS, "missing or unexpected manifest fields")
    require(type(manifest["schema_version"]) is int and manifest["schema_version"] == 1,
            "unsupported manifest schema")
    scope = manifest["hardware_scope"]
    require(isinstance(scope, dict) and set(scope) == {"label", "label_source", "instance_count", "pci_manufacturer_inference"},
            "invalid hardware scope")
    require(isinstance(scope["label"], str) and bool(scope["label"].strip()) and
            scope["label_source"] == "user_statement" and type(scope["instance_count"]) is int and
            scope["instance_count"] == 1 and scope["pci_manufacturer_inference"] is False,
            "hardware scope must describe the single user-identified board")
    expected = manifest["expected_snapshot"]
    require(isinstance(expected, dict), "missing expected snapshot")
    require(canonical_bytes(snapshot) == canonical_bytes(expected), "compiled snapshot differs from reviewed pins")
    require(type(snapshot.get("schema_version")) is int and snapshot["schema_version"] == 1 and
            snapshot.get("snapshot_kind") == "compiled_private_catalog" and
            type(snapshot.get("profile_count")) is int and snapshot["profile_count"] == 1,
            "invalid single-profile snapshot")
    require(snapshot.get("gsp") == {"state": "not_observed", "version": None},
            "this audit does not validate a GSP version")
    revision = snapshot.get("profile_revision")
    require(type(revision) is int and revision > 0, "invalid profile revision")
    history = manifest["revision_history"]
    require(isinstance(history, list) and len(history) > 0, "missing revision history")
    previous = 0
    for record in history:
        require(isinstance(record, dict) and set(record) == RECORD_FIELDS, "invalid revision record")
        number = record["revision"]
        require(type(number) is int and previous < number <= revision, "revision records must be strictly increasing")
        previous = number
        require(isinstance(record["recorded_on"], str) and re.fullmatch(r"\d{4}-\d{2}-\d{2}", record["recorded_on"]) is not None,
                "revision date must use YYYY-MM-DD")
        require(isinstance(record["change_summary"], str) and bool(record["change_summary"].strip()),
                "revision requires an explicit change summary")
        for field in RECORD_FIELDS - {"revision", "recorded_on", "change_summary"}:
            require(isinstance(record[field], str) and HEX_SHA256.fullmatch(record[field]) is not None,
                    f"invalid revision digest: {field}")
    active = history[-1]
    require(active["revision"] == revision, "active revision record is missing")
    digest_values = {
        "snapshot_sha256": expected,
        "policy_sources_sha256": manifest["policy_source_files"],
        "evidence_sources_sha256": manifest["evidence_sources"],
        "fixture_sources_sha256": manifest["fixture_sources"],
    }
    for field, value in digest_values.items():
        require(active[field] == canonical_sha256(value), f"active revision does not anchor {field}")
    verify_files(root, manifest["policy_source_files"], "policy")
    verify_files(root, manifest["evidence_sources"], "evidence")
    verify_files(root, manifest["fixture_sources"], "fixture", allow_empty=True)
    return {"schema_version": 1, "status": "verified", "profile_id": snapshot["profile_id"],
            "profile_revision": revision, "snapshot_sha256": canonical_sha256(snapshot),
            "policy_files": len(manifest["policy_source_files"]),
            "evidence_files": len(manifest["evidence_sources"]),
            "fixture_files": len(manifest["fixture_sources"]), "hardware_access": False}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, required=True, help="JSON emitted by rtxmon_private_catalog_snapshot")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1], help="repository root")
    parser.add_argument("--manifest", type=Path, help="reviewed audit manifest (defaults to docs/profiles/rtx3060-galax-12gb.json)")
    args = parser.parse_args(argv)
    try:
        root = args.root.resolve()
        manifest_path = args.manifest or root / "docs/profiles/rtx3060-galax-12gb.json"
        result = verify(load_json(args.snapshot), load_json(manifest_path), root)
        print(json.dumps(result, sort_keys=True, separators=(",", ":")))
        return 0
    except (AuditError, OSError, ValueError, TypeError, KeyError, RecursionError) as error:
        print(f"private-profile-audit: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
