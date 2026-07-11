# _util.py
#
# Date/time parsing helpers shared by the integration connectors, mirroring the
# exact C# semantics used across the CircleAI.Integration.* assemblies.
#
# C# ``DateTimeOffset.TryParse(s, InvariantCulture, DateTimeStyles.
# AssumeUniversal)`` then ``.ToUniversalTime()`` → parse ISO-8601 (offset-aware;
# offset-less strings are treated as UTC), then normalise to UTC. On failure the
# C# returns ``DateTimeOffset.MinValue``; we return :data:`DATETIME_MIN`.

from __future__ import annotations

from datetime import datetime, timezone
from email.utils import parsedate_to_datetime

from .contracts import DATETIME_MIN

# strptime fallbacks for the ISO-8601 shapes ``fromisoformat`` rejects on
# Python 3.10 (no fractional / seconds / offset variants). ``%z`` handles a
# trailing offset once "Z" has been normalised to "+00:00".
_ISO_FORMATS = (
    "%Y-%m-%dT%H:%M:%S.%f%z",
    "%Y-%m-%dT%H:%M:%S%z",
    "%Y-%m-%dT%H:%M%z",
    "%Y-%m-%dT%H:%M:%S.%f",
    "%Y-%m-%dT%H:%M:%S",
    "%Y-%m-%dT%H:%M",
    "%Y-%m-%d",
)


def parse_utc(s: str | None) -> datetime:
    """Mirror C# ``DateTimeOffset.TryParse(s, AssumeUniversal).ToUniversalTime()``.

    Accepts ISO-8601 (with/without offset, trailing ``Z``, fractional seconds)
    and RFC 1123 / 2822 dates (RSS ``pubDate``). Offset-less values are treated
    as UTC (``AssumeUniversal``). Returns :data:`DATETIME_MIN` when ``s`` is
    null/blank or unparseable.
    """
    if s is None:
        return DATETIME_MIN
    s = s.strip()
    if not s:
        return DATETIME_MIN
    # Normalise a trailing "Z" (and "+00:00Z") that older strptime/fromisoformat
    # do not accept, so "%z" / fromisoformat can consume the offset.
    iso = s[:-1] + "+00:00" if s.endswith("Z") else s
    dt = _try_fromisoformat(iso)
    if dt is None:
        for fmt in _ISO_FORMATS:
            try:
                dt = datetime.strptime(iso, fmt)
                break
            except ValueError:
                continue
    if dt is None:
        dt = _try_rfc(s)
    if dt is None:
        return DATETIME_MIN
    if dt.tzinfo is None:
        # AssumeUniversal: an offset-less timestamp is UTC.
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def _try_fromisoformat(s: str) -> datetime | None:
    try:
        return datetime.fromisoformat(s)
    except ValueError:
        return None


def _try_rfc(s: str) -> datetime | None:
    """RFC 1123 / 2822 (e.g. ``Mon, 01 Jan 2024 12:00:00 GMT``)."""
    try:
        return parsedate_to_datetime(s)
    except (TypeError, ValueError):
        return None


def parse_ics_time(v: str | None) -> datetime:
    """Mirror the CalDAV connector's ``Time(key)`` local function.

    Accepts ``yyyyMMddTHHmmssZ`` (UTC instant) or ``yyyyMMdd`` (date-only,
    midnight UTC). Returns :data:`DATETIME_MIN` on blank/unparseable input.
    """
    if not v:
        return DATETIME_MIN
    try:
        dt = datetime.strptime(v, "%Y%m%dT%H%M%SZ")
        return dt.replace(tzinfo=timezone.utc)
    except ValueError:
        pass
    try:
        d = datetime.strptime(v, "%Y%m%d")
        return d.replace(tzinfo=timezone.utc)
    except ValueError:
        return DATETIME_MIN


def parse_date_only(s: str | None) -> datetime | None:
    """Mirror ``DateOnly.TryParse`` for a ``yyyy-MM-dd`` all-day value.

    Returns midnight-UTC :class:`datetime` on success, else ``None``.
    """
    if not s:
        return None
    try:
        d = datetime.strptime(s.strip(), "%Y-%m-%d")
    except ValueError:
        return None
    return d.replace(tzinfo=timezone.utc)


def from_unix_millis(ms: int) -> datetime:
    """Mirror C# ``DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime``."""
    return datetime.fromtimestamp(ms / 1000.0, tz=timezone.utc)


def to_iso_o(dt: datetime) -> str:
    """Mirror C# ``DateTimeOffset.ToString("O")`` closely enough for request
    bodies: ISO-8601 with offset. The in-memory fetcher does not parse request
    bodies, so exact fractional-digit parity is not load-bearing.
    """
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.isoformat()
