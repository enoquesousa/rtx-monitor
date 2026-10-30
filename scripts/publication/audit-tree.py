#!/usr/bin/env python3
"""Fail-closed audit of an exact Git tree proposed for public release.

The report identifies paths and issue classes, never matching values. It is
one review gate, not a legal opinion or a guarantee against unknown secrets.
"""

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import tarfile
import zlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
POLICY = pathlib.Path(__file__).with_name("public-policy.json")
UUID_PATTERN = re.compile(
    rb"GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
    re.IGNORECASE,
)
SECRET_PATTERNS = {
    "private_key": re.compile(rb"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
    "github_token": re.compile(rb"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b"),
    "github_fine_grained_token": re.compile(rb"\bgithub_pat_[A-Za-z0-9_]{40,}\b"),
    "aws_access_key": re.compile(rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "openai_key": re.compile(rb"\bsk-[A-Za-z0-9_-]{32,}\b"),
}
PERSONAL_PATH = re.compile(
    rb"(?:[a-z]:/|/)(?:users|home)/[^/\x00\r\n\"']+",
    re.IGNORECASE,
)


def text_chunks_from_png(data: bytes):
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        return
    offset = 8
    while offset + 12 <= len(data):
        length = int.from_bytes(data[offset : offset + 4], "big")
        kind = data[offset + 4 : offset + 8]
        end = offset + 12 + length
        if end > len(data):
            return
        payload = data[offset + 8 : offset + 8 + length]
        if kind in {b"tEXt", b"eXIf"}:
            yield payload
        elif kind == b"zTXt":
            marker = payload.find(b"\x00")
            if marker >= 0 and marker + 2 < len(payload):
                try:
                    stream = zlib.decompressobj()
                    value = stream.decompress(payload[marker + 2 :], 1_000_000)
                    yield value if stream.eof else b"png_compressed_metadata_unreadable"
                except (ValueError, zlib.error):
                    yield b"png_compressed_metadata_unreadable"
        elif kind == b"iTXt":
            marker = payload.find(b"\x00")
            if marker >= 0 and marker + 3 <= len(payload):
                flags = payload[marker + 1 : marker + 3]
                rest = payload[marker + 3 :]
                language_end = rest.find(b"\x00")
                translated_end = rest.find(b"\x00", language_end + 1)
                if language_end < 0 or translated_end < 0:
                    yield b"png_compressed_metadata_unreadable"
                    continue
                value = rest[translated_end + 1 :]
                if flags[:1] == b"\x01":
                    try:
                        stream = zlib.decompressobj()
                        value = stream.decompress(value, 1_000_000)
                        if not stream.eof:
                            yield b"png_compressed_metadata_unreadable"
                            continue
                    except (ValueError, zlib.error):
                        yield b"png_compressed_metadata_unreadable"
                        continue
                yield value
        offset = end


def inspect_content(data: bytes, is_png: bool, synthetic_uuids=frozenset()):
    concerns = set()
    samples = [data]
    if is_png:
        samples.extend(text_chunks_from_png(data))
        if b"png_compressed_metadata_unreadable" in samples:
            concerns.add("png_metadata_unreadable")
    for sample in samples:
        normalized = re.sub(rb"/+", b"/", sample.lower().replace(b"\\", b"/"))
        if PERSONAL_PATH.search(normalized):
            concerns.add("personal_path")
        if any(
            match.decode("ascii").lower() not in synthetic_uuids
            for match in UUID_PATTERN.findall(sample)
        ):
            concerns.add("gpu_uuid")
        for name, pattern in SECRET_PATTERNS.items():
            if pattern.search(sample):
                concerns.add(name)
        # Detect common UTF-16LE paths used by Windows metadata.
        if b"\x00" in sample:
            for marker in (b"C\x00:\x00/\x00U\x00s\x00e\x00r\x00s\x00",
                           b"C\x00:\x00\\\x00U\x00s\x00e\x00r\x00s\x00"):
                if marker.lower() in sample.lower():
                    concerns.add("personal_path_utf16")
    return sorted(concerns)


def audit(tree: str):
    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    allowed_extensions = set(policy["allowed_extensions"])
    allowed_extensionless = set(policy["allowed_extensionless_names"])
    denied_extensions = set(policy["denied_extensions"])
    denied_prefixes = tuple(policy["denied_prefixes"])
    synthetic_uuids = {
        value.lower() for value in policy["allowed_synthetic_gpu_uuids"]
    }
    listing = subprocess.run(
        ["git", "ls-tree", "-r", "-z", "--name-only", tree],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=True,
    )
    expected = {
        name.decode("utf-8", "surrogateescape")
        for name in listing.stdout.split(b"\x00") if name
    }
    proc = subprocess.Popen(
        ["git", "archive", "--format=tar", tree],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    issues = []
    files = []
    archived_names = set()
    assert proc.stdout is not None
    with tarfile.open(fileobj=proc.stdout, mode="r|") as archive:
        for member in archive:
            if not member.isfile():
                continue
            name = member.name.replace("\\", "/")
            archived_names.add(name)
            lower = name.lower()
            path = pathlib.PurePosixPath(name)
            reasons = []
            if any(lower.startswith(prefix) for prefix in denied_prefixes):
                reasons.append("denied_path")
            if path.suffix.lower() in denied_extensions:
                reasons.append("denied_extension")
            if (path.suffix.lower() not in allowed_extensions
                    and path.name not in allowed_extensionless):
                reasons.append("not_allowlisted")
            entry = archive.extractfile(member)
            if entry is None:
                reasons.append("unreadable")
                data = b""
            else:
                data = entry.read()
            reasons.extend(
                inspect_content(data, lower.endswith(".png"), synthetic_uuids)
            )
            files.append({
                "path": name,
                "bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            })
            for reason in sorted(set(reasons)):
                issues.append({"path": name, "reason": reason})
    if proc.stdout is not None:
        proc.stdout.close()
    code = proc.wait()
    if code:
        error = proc.stderr.read().decode("utf-8", "replace")[:500] if proc.stderr else ""
        raise RuntimeError(f"git archive failed ({code}): {error}")
    for missing in sorted(expected - archived_names):
        issues.append({"path": missing, "reason": "not_in_archive"})
    for unexpected in sorted(archived_names - expected):
        issues.append({"path": unexpected, "reason": "not_in_git_tree"})
    return {
        "schema_version": 1,
        "tree": tree,
        "file_count": len(files),
        "total_bytes": sum(item["bytes"] for item in files),
        "issues": issues,
        "files": files,
        "publication_ready": not issues,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tree", required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    report = audit(args.tree)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"tree={args.tree} files={report['file_count']} "
        f"issues={len(report['issues'])} ready={report['publication_ready']}"
    )
    for issue in report["issues"][:25]:
        print(f"{issue['path']}: {issue['reason']}")
    if len(report["issues"]) > 25:
        print(f"... {len(report['issues']) - 25} more issues in the local report")
    raise SystemExit(0 if report["publication_ready"] else 1)


if __name__ == "__main__":
    main()
