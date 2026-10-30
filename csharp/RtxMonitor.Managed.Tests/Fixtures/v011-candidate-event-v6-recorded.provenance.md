# Historical candidate event v6

First event of the sealed v0.11 candidate capture from 2026-09-06 02:30 UTC. Preserved solely to test byte-for-byte historical storage/export compatibility. Its hotspot did not pass the external comparison; it must not be treated as current calibration evidence. The old report v1 retains rounded ABI millidegrees and null original driver raw.

Source: `evidence/v011-implementation-20260905/live-events.jsonl`, SHA-256 `6737974514912c9ae1d98cd0c8d534de0df0014321b9e6b9d2e58bf35a6e8bbc`. The outer SSE envelope was removed and the event serialized as compact UTF-8 JSON with Python. Values and provenance remain identical; whitespace and JSON escaping are fixture normalization. The regression preserves these exact fixture bytes after storage, reopening and export.
