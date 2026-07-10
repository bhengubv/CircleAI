# redacted_evidence_json_converter.py
#
# Port of CircleAI.Security.RedactedEvidenceJsonConverter (C# — the EXACT spec).
#
# Serialises every value of AnomalySignal.Evidence as the SHA-256 hex of its
# UTF-8 bytes instead of the raw content. The keys (evidence labels) are
# preserved so structured log sinks (Seq, Loki, OpenSearch) can still join
# entries by evidence shape, but the raw values — which may carry session
# tokens, payload fragments, or PII — never leave the process in clear text.
#
# Read side intentionally reverses to an EMPTY dictionary: incoming JSON cannot
# be trusted to carry the original cleartext, and round-tripping hashes back
# into the dictionary would mask whether the source-of-record is the in-process
# signal or a serialised copy.

from __future__ import annotations

import hashlib
from typing import Dict, Mapping, Optional


def _hash_redacted(raw: Optional[str]) -> str:
    """Return ``"sha256:" + lowercase-hex(SHA256(UTF8(raw)))``.

    An empty or ``None`` value maps to the bare tag ``"sha256:"`` — matching
    the C# ``string.IsNullOrEmpty`` fast-path exactly.
    """
    if not raw:
        return "sha256:"
    digest = hashlib.sha256(raw.encode("utf-8")).hexdigest()
    return "sha256:" + digest


class RedactedEvidenceJsonConverter:
    """Serialises :attr:`AnomalySignal.evidence` with every value replaced by
    the hex SHA-256 of its UTF-8 bytes.

    Mirrors the C# ``JsonConverter<IReadOnlyDictionary<string,string>>`` — a
    stateless converter exposing :meth:`write` (redact) and :meth:`read`
    (return empty). Instances are cheap and interchangeable.
    """

    def write(self, value: Optional[Mapping[str, str]]) -> Optional[Dict[str, str]]:
        """Return a new dict with every value redacted to its SHA-256 tag.

        ``None`` in → ``None`` out (mirrors ``writer.WriteNullValue()``).
        """
        if value is None:
            return None
        return {key: _hash_redacted(raw) for key, raw in value.items()}

    def read(self, token: object) -> Optional[Dict[str, str]]:
        """Tolerate inbound JSON but never trust the values.

        ``None`` (JSON ``null``) → ``None``; anything else → an empty dict,
        matching the C# ``Read`` which skips the token and returns ``{}``.
        """
        if token is None:
            return None
        return {}
