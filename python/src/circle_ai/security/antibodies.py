"""Telling somebody what has happened to them, and the gate in front of it.

AWARENESS, NOT ENFORCEMENT. Everything here reports what it SEES and nothing
acts on it. Collapsing the two would put the component that can read your files
in charge of blocking them, and the blast radius of a false positive goes from a
notification to a device that will not open its owner's documents.

THE CORPUS IS LOCAL AND SO IS THE MATCHING. A device does not ask a remote
service "has this address been breached", because that question tells the
service the address AND that its owner is worried.

NOTHING HERE IS A VERDICT. An assessment says what was observed and how
confident it is. "This file is safe" is a promise no local check can keep, and a
UI that renders one is lying on the product's behalf.
"""

from __future__ import annotations

import hashlib
import ipaddress
import os
import re
import secrets
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum, IntEnum
from typing import Callable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


class ThreatSeverity(IntEnum):
    """How bad, on ONE scale, so three different sources can be compared."""

    INFORMATIONAL = 0
    LOW = 1
    MEDIUM = 2
    HIGH = 3
    CRITICAL = 4


class ThreatAwarenessVerdict(Enum):
    """What an assessment concluded."""

    #: NO ASSESSMENT WAS PERFORMED — the gate denied it, or nothing ran. The
    #: DEFAULT, so an unset result reads as "nothing was checked" rather than as
    #: a pass.
    NOT_ASSESSED = "not-assessed"
    #: Did not match anything known-bad in the local corpus. NOT a clean bill of
    #: health: it means "no known threat", nothing stronger, and a UI that
    #: renders it as "safe" is lying on the product's behalf.
    NO_KNOWN_THREAT = "no-known-threat"
    SUSPICIOUS = "suspicious"
    KNOWN_BAD = "known-bad"


@dataclass(frozen=True)
class ThreatAwarenessResult:
    """One observation, said to a PERSON."""

    verdict: ThreatAwarenessVerdict
    severity: ThreatSeverity
    #: The line that appears in a notification, so it names the thing rather
    #: than the rule that fired.
    summary: str
    detail: str = ""
    #: Which corpus or check produced it. "Flagged" is not actionable without
    #: "by whom" — one source's false positive is another's deliberate policy.
    source: str = ""
    #: 0..1. Reported rather than thresholded here, because what counts as
    #: enough differs per surface: a banking screen and a photo gallery should
    #: not share one cutoff.
    confidence: float = 1.0
    at: datetime = field(default_factory=_now)


class IndicatorKind(Enum):
    """What an indicator describes."""

    IDENTITY = "identity"
    NETWORK = "network"
    FILE = "file"


@dataclass(frozen=True)
class IdentityIndicator:
    """One breach record."""

    #: An email address, phone number or handle — HASHED, never the value. The
    #: corpus never needs the original, and holding one turns a protective
    #: feature into a second copy of the thing being protected.
    identifier_sha256: str
    breach_name: str
    breach_at: datetime
    #: What was exposed: "password", "id number", "address". The part people
    #: actually need in order to decide what to change.
    exposed_fields: tuple[str, ...] = ()


@dataclass(frozen=True)
class NetworkIndicator:
    """One bad host or address."""

    value: str
    category: str
    severity: ThreatSeverity
    source: str = ""


@dataclass(frozen=True)
class FileArtifact:
    """A file being assessed."""

    path: str
    sha256: str = ""
    size_bytes: int = 0
    declared_mime: str = ""


@dataclass(frozen=True)
class IndicatorMatch:
    """A corpus hit."""

    kind: IndicatorKind
    value: str
    source: str
    severity: ThreatSeverity
    detail: str = ""


@dataclass(frozen=True)
class ThreatIndicator:
    """One thing a corpus flags."""

    kind: IndicatorKind
    value: str
    severity: ThreatSeverity
    source: str = ""
    detail: str = ""


def sha256_hex(text: str) -> str:
    """Lower-case hex SHA-256 of a string.

    Exposed because a caller holding an identifier should hash it ONCE and pass
    the hash around, rather than passing the plain value to three components.
    """
    return hashlib.sha256(text.strip().lower().encode()).hexdigest()


# ─────────────────────────────────────────────────────────────────────────────
# The corpus


class ILocalIndicatorCorpus(ABC):
    """A local set of indicators."""

    @property
    @abstractmethod
    def name(self) -> str: ...

    @abstractmethod
    def find_identity(self, identifier_sha256: str) -> IdentityIndicator | None: ...

    @abstractmethod
    def find_network(self, value: str) -> NetworkIndicator | None: ...

    @abstractmethod
    def find_file(self, sha256: str) -> IndicatorMatch | None: ...

    @abstractmethod
    def __len__(self) -> int: ...


class EmptyIndicatorCorpus(ILocalIndicatorCorpus):
    """Has nothing in it.

    THE DEFAULT, deliberately. Shipping a populated corpus would mean shipping
    somebody else's list and its politics; a host loads one it chose. Empty
    means every assessment comes back "nothing known", which is honest, rather
    than "clean", which is not.
    """

    @property
    def name(self) -> str:
        return "empty"

    def find_identity(self, identifier_sha256: str) -> IdentityIndicator | None:
        return None

    def find_network(self, value: str) -> NetworkIndicator | None:
        return None

    def find_file(self, sha256: str) -> IndicatorMatch | None:
        return None

    def __len__(self) -> int:
        return 0


class InMemoryIndicatorCorpus(ILocalIndicatorCorpus):
    """A corpus a host loaded."""

    def __init__(
        self,
        name: str,
        identities: Sequence[IdentityIndicator] = (),
        networks: Sequence[NetworkIndicator] = (),
        files: Sequence[IndicatorMatch] = (),
    ) -> None:
        self._name = name
        self._lock = threading.Lock()
        self._identities = {i.identifier_sha256.lower(): i for i in identities}
        self._networks = {n.value.lower(): n for n in networks}
        self._files = {f.value.lower(): f for f in files}

    @property
    def name(self) -> str:
        return self._name

    def find_identity(self, identifier_sha256: str) -> IdentityIndicator | None:
        with self._lock:
            return self._identities.get(identifier_sha256.lower())

    def find_network(self, value: str) -> NetworkIndicator | None:
        """Checks the host and then each PARENT DOMAIN.

        A corpus listing "bad.example" should match "tracker.bad.example", and
        one that only matches exactly is a corpus that never fires on anything
        real.
        """
        host = value.strip().lower()
        with self._lock:
            while True:
                found = self._networks.get(host)
                if found is not None:
                    return found
                head, sep, rest = host.partition(".")
                if not sep or "." not in rest:
                    return None
                host = rest

    def find_file(self, sha256: str) -> IndicatorMatch | None:
        with self._lock:
            return self._files.get(sha256.lower())

    def __len__(self) -> int:
        with self._lock:
            return len(self._identities) + len(self._networks) + len(self._files)


# ─────────────────────────────────────────────────────────────────────────────
# The assessors


class IBreachExposureAwareness(ABC):
    """Has the user's OWN identity turned up in a breach."""

    @abstractmethod
    def assess(self, identifier: str) -> Sequence[ThreatAwarenessResult]:
        """`identifier` is the PLAIN value; it is hashed here and the plain form
        never leaves this call."""


_HIGH_VALUE_FIELDS = frozenset({"password", "id number", "passport", "card number"})


class BreachExposureAssessor(IBreachExposureAwareness):
    """The default assessor."""

    def __init__(self, corpus: ILocalIndicatorCorpus | None = None) -> None:
        self._corpus = corpus or EmptyIndicatorCorpus()

    def assess(self, identifier: str) -> Sequence[ThreatAwarenessResult]:
        if not identifier.strip():
            raise ValueError("an identifier is required")
        found = self._corpus.find_identity(sha256_hex(identifier))
        if found is None:
            return (ThreatAwarenessResult(
                ThreatAwarenessVerdict.NO_KNOWN_THREAT, ThreatSeverity.INFORMATIONAL,
                "not in any breach set on this device",
                "this checks only what is stored locally, so it is not proof of anything",
                self._corpus.name,
            ),)
        # Severity follows WHAT WAS EXPOSED, not the age of the breach. A
        # password exposed five years ago that somebody still uses is a live
        # problem; an email address exposed yesterday mostly is not.
        severity = ThreatSeverity.MEDIUM
        if any(f.lower() in _HIGH_VALUE_FIELDS for f in found.exposed_fields):
            severity = ThreatSeverity.CRITICAL
        return (ThreatAwarenessResult(
            ThreatAwarenessVerdict.KNOWN_BAD, severity,
            f"this address appears in {found.breach_name}",
            "exposed: " + ", ".join(found.exposed_fields),
            self._corpus.name,
            # Not 1.0. A hash match is strong evidence the address was in the
            # set, and no evidence at all that the set is accurate.
            confidence=0.9,
        ),)


class IFileThreatAwareness(ABC):
    """Is a file the user is about to open known-bad."""

    @abstractmethod
    def assess(self, artifact: FileArtifact) -> Sequence[ThreatAwarenessResult]: ...


_EXECUTABLE_EXTENSIONS = frozenset({
    ".exe", ".scr", ".bat", ".cmd", ".com", ".msi", ".apk", ".jar", ".sh", ".ps1",
})

#: The right-to-left override and its friends. This is the trick that makes
#: "photo_annexe.exe" render as "photo_exe.ennexa", and no hash list catches a
#: file nobody has seen before.
_BIDI_OVERRIDES = "‮‫‭‪⁦⁧"


class FileThreatAwarenessAssessor(IFileThreatAwareness):
    """Hashes the file and asks the corpus, and separately notices the shapes
    that are suspicious regardless of any list."""

    def __init__(self, corpus: ILocalIndicatorCorpus | None = None) -> None:
        self._corpus = corpus or EmptyIndicatorCorpus()

    def assess(self, artifact: FileArtifact) -> Sequence[ThreatAwarenessResult]:
        """Empty is not a certificate.

        "No observations" and "clean" are the same answer here, and pretending
        to certify a file as safe is a promise no local check can keep.
        """
        out: list[ThreatAwarenessResult] = []

        if artifact.sha256:
            match = self._corpus.find_file(artifact.sha256)
            if match is not None:
                out.append(ThreatAwarenessResult(
                    ThreatAwarenessVerdict.KNOWN_BAD, match.severity,
                    "this file matches something the local list flags",
                    match.detail, match.source, confidence=0.95,
                ))

        name = os.path.basename(artifact.path)
        if any(ch in name for ch in _BIDI_OVERRIDES):
            out.append(ThreatAwarenessResult(
                ThreatAwarenessVerdict.SUSPICIOUS, ThreatSeverity.HIGH,
                "this file name is written to display backwards",
                "it contains a right-to-left override, which hides the real extension",
                "shape", confidence=0.95,
            ))

        lower = name.lower()
        stem, ext = os.path.splitext(lower)
        if ext in _EXECUTABLE_EXTENSIONS:
            inner = os.path.splitext(stem)[1]
            if inner and inner not in _EXECUTABLE_EXTENSIONS:
                out.append(ThreatAwarenessResult(
                    ThreatAwarenessVerdict.SUSPICIOUS, ThreatSeverity.HIGH,
                    "this looks like a document but will run as a program",
                    f"the name ends {inner}{ext}", "shape", confidence=0.9,
                ))
            if artifact.declared_mime and not artifact.declared_mime.startswith("application/"):
                out.append(ThreatAwarenessResult(
                    ThreatAwarenessVerdict.SUSPICIOUS, ThreatSeverity.MEDIUM,
                    "this file says it is one thing and is named as another",
                    f"declared {artifact.declared_mime}, named {ext}",
                    "shape", confidence=0.7,
                ))

        if not out:
            out.append(ThreatAwarenessResult(
                ThreatAwarenessVerdict.NO_KNOWN_THREAT, ThreatSeverity.INFORMATIONAL,
                "nothing known about this file", "that is not the same as safe",
                self._corpus.name,
            ))
        out.sort(key=lambda r: r.severity, reverse=True)
        return tuple(out)


class INetworkThreatAwareness(ABC):
    """Is a host about to be trusted known-bad."""

    @abstractmethod
    def assess(self, host_or_address: str) -> Sequence[ThreatAwarenessResult]: ...


class NetworkThreatAwarenessAssessor(INetworkThreatAwareness):
    """Checks a host against the local corpus."""

    def __init__(self, corpus: ILocalIndicatorCorpus | None = None) -> None:
        self._corpus = corpus or EmptyIndicatorCorpus()

    def assess(self, host_or_address: str) -> Sequence[ThreatAwarenessResult]:
        if not host_or_address.strip():
            raise ValueError("a host or address is required")
        found = self._corpus.find_network(host_or_address)
        if found is not None:
            return (ThreatAwarenessResult(
                ThreatAwarenessVerdict.KNOWN_BAD, found.severity,
                f"{host_or_address} is on a {found.category} list",
                source=found.source, confidence=0.9,
            ),)
        return (ThreatAwarenessResult(
            ThreatAwarenessVerdict.NO_KNOWN_THREAT, ThreatSeverity.INFORMATIONAL,
            f"nothing known about {host_or_address}",
            "the local lists have no entry; that is not the same as safe",
            self._corpus.name,
        ),)


# ─────────────────────────────────────────────────────────────────────────────
# The gate


class AntibodyCapability(Enum):
    """What may be asked of the awareness layer."""

    #: "Is a file the user is about to open known-bad?" A pre-open warning about
    #: somebody's own downloads.
    FILE_REPUTATION_AWARENESS = "file-reputation-awareness"
    #: "Is a host about to be trusted known-bad?" A pre-connect warning, not a
    #: block.
    NETWORK_INDICATOR_AWARENESS = "network-indicator-awareness"
    #: "Has the user's OWN identity turned up in a breach?" Their own identity
    #: ONLY — the capability does not exist for looking up anybody else.
    BREACH_EXPOSURE_AWARENESS = "breach-exposure-awareness"


@dataclass(frozen=True)
class AuthorizedUseConsent:
    """Permission for one capability, bounded and attributed."""

    consent_id: str
    capability: AntibodyCapability
    granted_by: str
    scope: str
    granted_at: datetime
    expires_at: datetime

    def is_active_for(self, capability: AntibodyCapability, now: datetime) -> bool:
        """Half-open: the expiry instant is already lapsed."""
        return (
            self.capability is capability
            and self.granted_at <= now < self.expires_at
        )

    @classmethod
    def grant(
        cls,
        capability: AntibodyCapability,
        granted_by: str,
        scope: str,
        duration: timedelta,
        now: datetime | None = None,
    ) -> "AuthorizedUseConsent":
        """Grants for a bounded duration starting now.

        Raises for a blank granter, a blank scope, or a non-positive duration.
        An unattributed or unbounded consent is not a STRICTER grant — it is a
        permission that cannot be reviewed, revoked on schedule, or explained to
        the person it was taken on behalf of.
        """
        if not granted_by.strip():
            raise ValueError(
                "a granter is required: 'the system consented' is how this "
                "becomes surveillance with a changelog"
            )
        if not scope.strip():
            raise ValueError("a scope is required")
        if duration <= timedelta():
            raise ValueError(
                "a positive duration is required: a permission that never "
                "lapses is one nobody remembers giving"
            )
        at = now or _now()
        return cls(
            consent_id=secrets.token_hex(8), capability=capability,
            granted_by=granted_by, scope=scope,
            granted_at=at, expires_at=at + duration,
        )


@dataclass(frozen=True)
class AuthorizedUseRequest:
    """One thing being asked."""

    capability: AntibodyCapability
    #: A hash, a host, a hashed identifier — never the plain identity value.
    subject: str
    scope: str = ""
    requested_by: str = ""
    at: datetime = field(default_factory=_now)


@dataclass(frozen=True)
class AuthorizationDecision:
    """The answer."""

    allowed: bool
    #: ALWAYS populated, including when allowed. A decision without a reason
    #: cannot be shown to the person it was made about, and this is the one
    #: component where that is the whole point.
    reason: str
    consent_id: str = ""
    at: datetime = field(default_factory=_now)


class IAuthorizedUseConsentStore(ABC):
    """Holds consents."""

    @abstractmethod
    def put(self, consent: AuthorizedUseConsent) -> None: ...

    @abstractmethod
    def find_active(
        self, capability: AntibodyCapability, now: datetime
    ) -> AuthorizedUseConsent | None: ...

    @abstractmethod
    def revoke(self, consent_id: str) -> bool:
        """IMMEDIATE, and there is no soft-delete. A consent somebody withdrew
        must stop working the moment they say so."""

    @abstractmethod
    def __len__(self) -> int: ...


class InMemoryAuthorizedUseConsentStore(IAuthorizedUseConsentStore):
    """The default store."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._consents: dict[str, AuthorizedUseConsent] = {}

    def put(self, consent: AuthorizedUseConsent) -> None:
        with self._lock:
            self._consents[consent.consent_id] = consent

    def find_active(
        self, capability: AntibodyCapability, now: datetime
    ) -> AuthorizedUseConsent | None:
        with self._lock:
            for consent in self._consents.values():
                if consent.is_active_for(capability, now):
                    return consent
        return None

    def revoke(self, consent_id: str) -> bool:
        with self._lock:
            return self._consents.pop(consent_id, None) is not None

    def __len__(self) -> int:
        with self._lock:
            return len(self._consents)


class IAuthorizedUseGate(ABC):
    """Decides whether an assessment may happen at all."""

    @abstractmethod
    def authorize(self, request: AuthorizedUseRequest) -> AuthorizationDecision: ...


class NullAuthorizedUseGate(IAuthorizedUseGate):
    """Denies everything.

    THE DEFAULT. Not a test double: a host that wires nothing should get a
    component that assesses nothing. The alternative default — allow when
    unconfigured — is a capability that reads files because somebody forgot a
    line of setup.
    """

    def authorize(self, request: AuthorizedUseRequest) -> AuthorizationDecision:
        return AuthorizationDecision(
            False, "no authorization gate is configured, so nothing is assessed"
        )


class ExplicitConsentAuthorizedUseGate(IAuthorizedUseGate):
    """Allows only what an active consent covers."""

    def __init__(self, store: IAuthorizedUseConsentStore) -> None:
        self._store = store

    def authorize(self, request: AuthorizedUseRequest) -> AuthorizationDecision:
        now = request.at or _now()
        consent = self._store.find_active(request.capability, now)
        if consent is None:
            return AuthorizationDecision(
                False,
                f"nobody has agreed to {request.capability.value} on this device",
                at=now,
            )
        return AuthorizationDecision(
            True,
            f"covered by consent granted by {consent.granted_by} for {consent.scope}",
            consent.consent_id, now,
        )


@dataclass(frozen=True)
class DefensiveThreatContext:
    """What an assessment observed and where."""

    severity: ThreatSeverity
    summary: str
    source: str = ""
    at: datetime = field(default_factory=_now)
    #: Local by default and by design: asking a remote service whether an
    #: address has been breached tells that service the address AND that its
    #: owner is worried.
    assessed_locally: bool = True


class IDefensiveAntibodySystem(ABC):
    """The assembled awareness layer."""

    @abstractmethod
    def assess_file(
        self, artifact: FileArtifact
    ) -> tuple[ThreatAwarenessVerdict, DefensiveThreatContext]: ...

    @abstractmethod
    def assess_network(
        self, host_or_address: str
    ) -> tuple[ThreatAwarenessVerdict, DefensiveThreatContext]: ...

    @abstractmethod
    def assess_breach_exposure(
        self, identifier: str
    ) -> tuple[ThreatAwarenessVerdict, DefensiveThreatContext]: ...


class DefensiveAntibodySystem(IDefensiveAntibodySystem):
    """The gate in front and the local corpus behind.

    AWARENESS, NEVER ENFORCEMENT. Nothing here blocks, quarantines or deletes.
    """

    def __init__(
        self,
        gate: IAuthorizedUseGate | None = None,
        corpus: ILocalIndicatorCorpus | None = None,
    ) -> None:
        self._gate = gate or NullAuthorizedUseGate()
        self._corpus = corpus or EmptyIndicatorCorpus()

    def _decide(
        self, capability: AntibodyCapability, subject: str
    ) -> AuthorizationDecision:
        return self._gate.authorize(
            AuthorizedUseRequest(capability=capability, subject=subject)
        )

    @staticmethod
    def _refused(decision: AuthorizationDecision) -> tuple[ThreatAwarenessVerdict, DefensiveThreatContext]:
        # NOT_ASSESSED, never a verdict inferred from having been stopped.
        return ThreatAwarenessVerdict.NOT_ASSESSED, DefensiveThreatContext(
            ThreatSeverity.INFORMATIONAL, decision.reason, at=decision.at
        )

    @staticmethod
    def _from(result: ThreatAwarenessResult) -> tuple[ThreatAwarenessVerdict, DefensiveThreatContext]:
        return result.verdict, DefensiveThreatContext(
            result.severity, result.summary, result.source, result.at
        )

    def assess_file(self, artifact: FileArtifact):
        decision = self._decide(
            AntibodyCapability.FILE_REPUTATION_AWARENESS, artifact.sha256
        )
        if not decision.allowed:
            return self._refused(decision)
        results = FileThreatAwarenessAssessor(self._corpus).assess(artifact)
        return self._from(results[0])

    def assess_network(self, host_or_address: str):
        decision = self._decide(
            AntibodyCapability.NETWORK_INDICATOR_AWARENESS, host_or_address
        )
        if not decision.allowed:
            return self._refused(decision)
        results = NetworkThreatAwarenessAssessor(self._corpus).assess(host_or_address)
        return self._from(results[0])

    def assess_breach_exposure(self, identifier: str):
        """`identifier` is hashed before it leaves this call; the plain value is
        never stored and never sent anywhere."""
        decision = self._decide(
            AntibodyCapability.BREACH_EXPOSURE_AWARENESS, sha256_hex(identifier)
        )
        if not decision.allowed:
            return self._refused(decision)
        results = BreachExposureAssessor(self._corpus).assess(identifier)
        return self._from(results[0])


# ─────────────────────────────────────────────────────────────────────────────
# Network defence


class ThreatDirection(Enum):
    """Which way the traffic went."""

    INBOUND = "inbound"
    #: The one that matters most on a personal device: something on this phone
    #: talking to somewhere it should not is a compromised app, and it is the
    #: case a defence aimed at servers is not looking for.
    OUTBOUND = "outbound"
    LATERAL = "lateral"


class ThreatCategory(Enum):
    """What kind of behaviour was seen."""

    SCANNING = "scanning"
    EXFILTRATION = "exfiltration"
    COMMAND_AND_CONTROL = "command-and-control"
    CREDENTIAL_ACCESS = "credential-access"
    DENIAL_OF_SERVICE = "denial-of-service"
    ANOMALY = "anomaly"


@dataclass(frozen=True)
class NetworkObservation:
    """One connection seen."""

    at: datetime
    local_endpoint: str
    remote_endpoint: str
    protocol: str = ""
    bytes_out: int = 0
    bytes_in: int = 0
    direction: ThreatDirection = ThreatDirection.OUTBOUND


class INetworkObservationFeed(ABC):
    """Supplies observations."""

    @abstractmethod
    def drain(self) -> Sequence[NetworkObservation]: ...


@dataclass(frozen=True)
class ThreatSignal:
    """Something worth telling a person about."""

    category: ThreatCategory
    direction: ThreatDirection
    severity: ThreatSeverity
    summary: str
    evidence: str = ""
    confidence: float = 1.0
    at: datetime = field(default_factory=_now)


@dataclass(frozen=True)
class Ipv4Cidr:
    """An IPv4 network."""

    network: ipaddress.IPv4Network

    @classmethod
    def parse(cls, text: str) -> "Ipv4Cidr | None":
        """Parses "a.b.c.d/n". A bare address is treated as /32.

        `strict=False` masks the base to the prefix: without it 10.0.0.5/8 is a
        ValueError, and with a hand-rolled parser it would compare unequal to
        10.0.0.0/8 — two entries for one network.
        """
        try:
            return cls(ipaddress.IPv4Network(text.strip(), strict=False))
        except (ipaddress.AddressValueError, ipaddress.NetmaskValueError, ValueError):
            return None

    def contains(self, address: str) -> bool:
        try:
            return ipaddress.IPv4Address(address) in self.network
        except (ipaddress.AddressValueError, ValueError):
            return False

    def __str__(self) -> str:
        return str(self.network)


@dataclass(frozen=True)
class ParsedIndicator:
    """One line of a blocklist."""

    kind: IndicatorKind
    value: str
    cidr: Ipv4Cidr | None = None
    comment: str = ""


class BlocklistParser:
    """Reads the common blocklist formats.

    Handles hosts-file lines, bare domains, addresses and CIDRs, because the
    lists people actually publish are a mix of all four — and a parser that
    handles one of them silently ignores most of the file.
    """

    _SINK_ADDRESSES = frozenset({"0.0.0.0", "127.0.0.1", "::1"})

    @classmethod
    def parse(cls, body: str) -> list[ParsedIndicator]:
        out: list[ParsedIndicator] = []
        for raw in body.splitlines():
            line = raw.strip()
            if not line or line[0] in "#;":
                continue
            comment = ""
            for marker in "#;":
                if marker in line:
                    line, _, comment = line.partition(marker)
                    line, comment = line.strip(), comment.strip()
                    break
            fields = line.split()
            if not fields:
                continue
            # A hosts-file line is "0.0.0.0 bad.example" — the interesting half
            # is the SECOND field. Taking the first would blocklist 0.0.0.0.
            value = fields[0]
            if len(fields) >= 2 and value in cls._SINK_ADDRESSES:
                value = fields[1]

            cidr = Ipv4Cidr.parse(value)
            if cidr is not None:
                out.append(ParsedIndicator(IndicatorKind.NETWORK, str(cidr), cidr, comment))
            elif "." in value:
                out.append(ParsedIndicator(IndicatorKind.NETWORK, value.lower(), None, comment))
        return out


class IIndicatorSource(ABC):
    """Supplies indicators to the defence layer."""

    @property
    @abstractmethod
    def name(self) -> str: ...

    @abstractmethod
    def load(self) -> Sequence[ParsedIndicator]: ...


class BlocklistIndicatorSource(IIndicatorSource):
    """Loads indicators from a blocklist body.

    LOCAL by default: the body comes from a file a host chose. Fetching a list
    over the network would mean the defence layer tells somebody's server which
    device is running it, every time it refreshes.
    """

    def __init__(self, name: str, read: Callable[[], str] | None = None) -> None:
        self._name = name
        self._read = read

    @property
    def name(self) -> str:
        return self._name

    def load(self) -> Sequence[ParsedIndicator]:
        if self._read is None:
            return ()
        return tuple(BlocklistParser.parse(self._read()))


class IThreatSink(ABC):
    """Receives threat signals."""

    @abstractmethod
    def report(self, signal: ThreatSignal) -> None: ...


class NullThreatSink(IThreatSink):
    """Receives and discards."""

    def report(self, signal: ThreatSignal) -> None:
        return None


class DelegateThreatSink(IThreatSink):
    """Calls a function."""

    def __init__(self, fn: Callable[[ThreatSignal], None] | None = None) -> None:
        self._fn = fn

    def report(self, signal: ThreatSignal) -> None:
        if self._fn is not None:
            self._fn(signal)


class CompositeThreatSink(IThreatSink):
    """Fans out to several sinks."""

    def __init__(self, *sinks: IThreatSink) -> None:
        self._sinks = [s for s in sinks if s is not None]

    def report(self, signal: ThreatSignal) -> None:
        """A raising sink must not stop the others: one broken reporter should
        not mean nobody is told about the threat."""
        for sink in self._sinks:
            try:
                sink.report(signal)
            except Exception:
                continue


class ISosEscalation(ABC):
    """Reaches a PERSON."""

    @abstractmethod
    def escalate(self, signal: ThreatSignal) -> bool:
        """False when it could not reach anybody, which the caller must handle
        rather than assume: an escalation nobody received is the failure this
        whole path exists to prevent."""


class NullSosEscalation(ISosEscalation):
    """Escalates nowhere and says so by returning False.

    THE DEFAULT, and False rather than True on purpose. A null escalation that
    reported success would make a device look protected while every alert went
    into nothing — which is worse than no defence at all, because somebody
    believes in it.
    """

    def escalate(self, signal: ThreatSignal) -> bool:
        return False


class DelegateSosEscalation(ISosEscalation):
    """Calls the host's function.

    The whole seam: what "reach a person" means — a notification, an SMS, a call
    to a neighbour — is the host's to decide.
    """

    def __init__(self, deliver: Callable[[ThreatSignal], bool] | None = None) -> None:
        self._deliver = deliver

    def escalate(self, signal: ThreatSignal) -> bool:
        if self._deliver is None:
            return False
        return bool(self._deliver(signal))


class SosThreatSink(IThreatSink):
    """Collects signals and escalates the ones that warrant it.

    De-duplicates within a window: the same finding arriving forty times is one
    situation, and forty alerts is how somebody learns to ignore all of them.
    """

    def __init__(
        self,
        escalation: ISosEscalation | None = None,
        minimum_severity: ThreatSeverity = ThreatSeverity.MEDIUM,
        dedupe_window: timedelta = timedelta(minutes=10),
    ) -> None:
        self._escalation = escalation or NullSosEscalation()
        self._minimum = minimum_severity
        self._window = dedupe_window
        self._lock = threading.Lock()
        self._last_seen: dict[str, datetime] = {}
        self._escalated = 0
        self._suppressed = 0

    def report(self, signal: ThreatSignal) -> None:
        self.submit(signal)

    def submit(self, signal: ThreatSignal) -> bool:
        if signal.severity < self._minimum:
            with self._lock:
                self._suppressed += 1
            return False
        key = f"{signal.category.value}|{signal.summary}"
        now = _now()
        with self._lock:
            last = self._last_seen.get(key)
            if last is not None and now - last < self._window:
                self._suppressed += 1
                return False
            self._last_seen[key] = now
        ok = self._escalation.escalate(signal)
        with self._lock:
            if ok:
                self._escalated += 1
            else:
                self._suppressed += 1
        return ok

    @property
    def counts(self) -> tuple[int, int]:
        """Escalated and suppressed."""
        with self._lock:
            return self._escalated, self._suppressed


class WatchdogThreatSink(IThreatSink):
    """Escalates through the SOS path."""

    def __init__(self, sink: SosThreatSink) -> None:
        self._sink = sink

    def report(self, signal: ThreatSignal) -> None:
        self._sink.submit(signal)


class IThreatMonitor(ABC):
    """Watches for something worth reporting."""

    @property
    @abstractmethod
    def name(self) -> str: ...

    @abstractmethod
    def observe(self, observation: NetworkObservation) -> None: ...


class BlocklistThreatMonitor(IThreatMonitor):
    """Checks observations against loaded indicators."""

    def __init__(self, sink: IThreatSink | None = None) -> None:
        self._sink = sink or NullThreatSink()
        self._lock = threading.Lock()
        self._domains: dict[str, ParsedIndicator] = {}
        self._cidrs: list[ParsedIndicator] = []

    @property
    def name(self) -> str:
        return "blocklist"

    def load_from(self, source: IIndicatorSource) -> None:
        with self._lock:
            for indicator in source.load():
                if indicator.cidr is not None:
                    self._cidrs.append(indicator)
                else:
                    self._domains[indicator.value] = indicator

    def observe(self, observation: NetworkObservation) -> None:
        """OUTBOUND matches are reported at a higher severity than inbound.

        Something on this phone talking to a known-bad host is a compromised
        app, and it is the case a defence aimed at servers is not looking for.
        """
        host = observation.remote_endpoint.rsplit(":", 1)[0]
        with self._lock:
            found = self._domains.get(host.lower())
            if found is None:
                found = next((c for c in self._cidrs if c.cidr and c.cidr.contains(host)), None)
        if found is None:
            return

        severity = ThreatSeverity.MEDIUM
        category = ThreatCategory.ANOMALY
        if observation.direction is ThreatDirection.OUTBOUND:
            severity = ThreatSeverity.HIGH
            category = ThreatCategory.COMMAND_AND_CONTROL
        self._sink.report(ThreatSignal(
            category, observation.direction, severity,
            f"this device connected to {host}, which is on a blocklist",
            found.comment, confidence=0.8,
        ))


@dataclass(frozen=True)
class DefenseOptions:
    """Configures the defence layer."""

    poll_interval: timedelta = timedelta(seconds=5)
    minimum_severity: ThreatSeverity = ThreatSeverity.MEDIUM
    dedupe_window: timedelta = timedelta(minutes=10)
    #: OFF by default. A defence layer that starts watching because it was
    #: imported is one nobody chose.
    enabled: bool = False


class IAutonomicDefense(ABC):
    """Watches continuously."""

    @abstractmethod
    def start(self) -> None: ...

    @abstractmethod
    def stop(self) -> None: ...

    @property
    @abstractmethod
    def is_running(self) -> bool: ...


class AlwaysOnDefenseSentinel(IAutonomicDefense):
    """Drains the feed and hands observations to the monitors.

    "Always on" describes what it does ONCE STARTED, not whether it starts on
    its own. It does not: a component that watches a device's network traffic
    should begin because somebody said so.
    """

    def __init__(
        self,
        options: DefenseOptions,
        feed: INetworkObservationFeed,
        monitors: Sequence[IThreatMonitor] = (),
    ) -> None:
        self._options = options
        self._feed = feed
        self._monitors = list(monitors)
        self._lock = threading.Lock()
        self._running = False

    def start(self) -> None:
        with self._lock:
            if not self._options.enabled:
                raise RuntimeError("the defence layer is off; enable it deliberately")
            self._running = True

    def stop(self) -> None:
        with self._lock:
            self._running = False

    @property
    def is_running(self) -> bool:
        with self._lock:
            return self._running

    def poll_once(self) -> int:
        """Drains the feed once. Returns how many observations were handled."""
        if not self.is_running:
            return 0
        handled = 0
        for observation in self._feed.drain():
            for monitor in self._monitors:
                monitor.observe(observation)
            handled += 1
        return handled


@dataclass
class DefenseModule:
    """The defence layer, assembled."""

    options: DefenseOptions
    sentinel: AlwaysOnDefenseSentinel
    sink: IThreatSink

    @classmethod
    def build(
        cls,
        options: DefenseOptions,
        feed: INetworkObservationFeed,
        escalation: ISosEscalation | None = None,
    ) -> "DefenseModule":
        sos = SosThreatSink(
            escalation or NullSosEscalation(),
            options.minimum_severity, options.dedupe_window,
        )
        sink = CompositeThreatSink(WatchdogThreatSink(sos))
        monitor = BlocklistThreatMonitor(sink)
        return cls(options, AlwaysOnDefenseSentinel(options, feed, [monitor]), sink)


class ThreatDetector:
    """Finds indicators in observed traffic."""

    def __init__(self, corpus: ILocalIndicatorCorpus | None = None) -> None:
        self._corpus = corpus or EmptyIndicatorCorpus()

    def detect_indicators(self, hosts: Sequence[str]) -> list[ThreatIndicator]:
        out: list[ThreatIndicator] = []
        for host in hosts:
            found = self._corpus.find_network(host)
            if found is not None:
                out.append(ThreatIndicator(
                    IndicatorKind.NETWORK, host, found.severity,
                    found.source, found.category,
                ))
        return out
