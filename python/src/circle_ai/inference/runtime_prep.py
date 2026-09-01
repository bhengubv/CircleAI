"""Getting a model onto the device, and knowing why when that fails.

THE DOWNLOAD IS THE EXPENSIVE PART, and not in seconds. A 4 GB model on a South
African mobile bundle is real money, and a gate that defaults to "go ahead"
spends somebody else's airtime. So the gate defaults to REFUSING on a metered
link and says what it would cost.

"DOWNLOAD FAILED" IS NOT A DIAGNOSIS. It sends a person to reboot a router when
the real answer is a captive portal, a clock so wrong that TLS refuses, or a
disk with no room. The preflight here distinguishes those, because each has a
different fix and only one of them is the router.

WHAT IS NOT HERE: no model name is hardcoded anywhere. The catalogue supplies
them. A hardcoded name is a model that cannot be replaced without a release.
"""

from __future__ import annotations

import hashlib
import os
import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum, IntEnum
from typing import Callable, Sequence


# ─────────────────────────────────────────────────────────────────────────────
# Failures


class ModelDownloadException(Exception):
    """A download failed for a reason that is not policy.

    Carries the model id and the FAULT, so a caller can retry the right thing
    instead of retrying everything.
    """

    def __init__(self, model_id: str, message: str, fault: "NetworkFault | None" = None) -> None:
        super().__init__(message)
        self.model_id = model_id
        self.fault = fault


class ModelDownloadBlockedException(ModelDownloadException):
    """A download was REFUSED, not failed.

    A separate type because the two demand opposite handling: a failure should
    be retried, and a refusal must never be - retrying a refusal in a loop is
    how a gate gets worn down until somebody disables it.
    """

    def __init__(self, model_id: str, reason: str, estimated_bytes: int = 0) -> None:
        super().__init__(model_id, reason)
        self.reason = reason
        self.estimated_bytes = estimated_bytes


# ─────────────────────────────────────────────────────────────────────────────
# The gate


class IModelDownloadGate(ABC):
    """Decides whether a model may be fetched now."""

    @abstractmethod
    def may_download(self, model_id: str, size_bytes: int) -> tuple[bool, str]:
        """Returns (allowed, reason). The reason is ALWAYS populated, including
        on allow, so a log says why something was permitted and not only why it
        was stopped."""


class AlwaysAllowDownloadGate(IModelDownloadGate):
    """Allows everything.

    Named so that choosing it is a visible decision. This is the right gate on a
    desktop on a fixed line and the wrong one on a phone.
    """

    def may_download(self, model_id: str, size_bytes: int) -> tuple[bool, str]:
        return True, "no gate is configured on this device"


@dataclass(frozen=True)
class NetworkConditions:
    """What the link looks like right now."""

    is_connected: bool = False
    #: The operating system's word for it. Trusted when true and NOT trusted
    #: when false: a phone tethering to another phone reports unmetered while
    #: spending the other phone's bundle.
    is_metered: bool = False
    is_roaming: bool = False
    #: True only for a link known to be free - Wi-Fi on a fixed line. Unknown
    #: counts as NOT free.
    is_known_unmetered: bool = False
    estimated_kbps: int = 0


class MeteredNetworkDownloadGate(IModelDownloadGate):
    """Refuses a large download on a link that costs money.

    THE DEFAULT IS REFUSE ON ANYTHING NOT KNOWN TO BE FREE, which is stricter
    than refusing on "metered". The operating system reports unmetered for a
    tether, so trusting it spends the other phone's bundle - which is the exact
    case this is written for.

    A small download passes: refusing a 2 MB configuration file on a mobile link
    would make the device useless while saving nothing.
    """

    #: Below this, the cost is not worth an interruption. Roughly a photograph.
    FREE_PASS_BYTES = 4 * 1024 * 1024

    def __init__(
        self,
        conditions: Callable[[], NetworkConditions] | None = None,
        consented_model_ids: Sequence[str] = (),
    ) -> None:
        self._conditions = conditions
        #: Consent is PER MODEL. Agreeing to spend a bundle on one model is not
        #: agreeing to spend it on every model the catalogue later carries.
        self._consented = {m.strip().lower() for m in consented_model_ids if m.strip()}

    def consent(self, model_id: str) -> None:
        self._consented.add(model_id.strip().lower())

    @staticmethod
    def describe_size(size_bytes: int) -> str:
        """In the units a person thinks in. A bundle is sold in gigabytes, so a
        refusal that says "4194304000 bytes" has not communicated anything."""
        if size_bytes >= 1 << 30:
            return f"{size_bytes / (1 << 30):.1f} GB"
        if size_bytes >= 1 << 20:
            return f"{size_bytes / (1 << 20):.0f} MB"
        return f"{max(1, size_bytes // 1024)} KB"

    def may_download(self, model_id: str, size_bytes: int) -> tuple[bool, str]:
        conditions = self._conditions() if self._conditions else NetworkConditions()
        if not conditions.is_connected:
            return False, "this device is not on a network"
        if size_bytes <= self.FREE_PASS_BYTES:
            return True, "small enough that the link does not matter"
        if model_id.strip().lower() in self._consented:
            return True, f"you agreed to fetch {model_id} on this connection"
        if conditions.is_roaming:
            # Roaming is checked BEFORE metered: a roaming link may report
            # unmetered and still cost more per megabyte than anything else a
            # person will pay for this year.
            return False, (
                f"{self.describe_size(size_bytes)} while roaming would be "
                f"expensive - this can wait for Wi-Fi")
        if not conditions.is_known_unmetered:
            return False, (
                f"this would use about {self.describe_size(size_bytes)} of your "
                f"data - ask again on Wi-Fi, or say to go ahead")
        return True, "on a connection known to be free"


# ─────────────────────────────────────────────────────────────────────────────
# Preflight


class NetworkFault(Enum):
    """Why a fetch could not happen. Each has a DIFFERENT fix."""

    NONE = "none"
    #: No link at all. The one people expect, and the least common in practice.
    OFFLINE = "offline"
    #: Name resolution failed while the link is up. Usually a DNS server that
    #: went away, not a dead connection.
    DNS = "dns"
    #: A network that answers everything with a login page. Looks like a
    #: successful fetch of the wrong bytes, which is why it is checked for
    #: explicitly rather than inferred from a failure.
    CAPTIVE_PORTAL = "captive-portal"
    #: TLS refused. Very often a device clock that is wrong by more than the
    #: certificate's validity - a fault that has nothing to do with the network
    #: and is never guessed correctly.
    TLS = "tls"
    #: The far side said no. Not our problem to retry.
    SERVER = "server"
    #: The link works and the download would cost money.
    METERED = "metered"
    #: The link works and the disk does not have room.
    NO_SPACE = "no-space"
    TIMEOUT = "timeout"


@dataclass(frozen=True)
class NetworkDiagnosis:
    """What is actually wrong, and what to do about it."""

    fault: NetworkFault = NetworkFault.NONE
    #: Written for a PERSON, not a log. This is shown on a screen.
    message: str = ""
    #: What they can do. Empty when there is nothing they can do, which is
    #: itself worth saying rather than implying.
    suggestion: str = ""
    can_retry: bool = True

    @property
    def is_healthy(self) -> bool:
        return self.fault is NetworkFault.NONE

    @staticmethod
    def healthy() -> "NetworkDiagnosis":
        return NetworkDiagnosis()

    @staticmethod
    def of(fault: NetworkFault) -> "NetworkDiagnosis":
        """The standard wording for each fault, so the same problem reads the
        same way wherever it surfaces."""
        table = {
            NetworkFault.OFFLINE: (
                "this device is not on a network",
                "connect to Wi-Fi or turn on mobile data", True),
            NetworkFault.DNS: (
                "the network is up but names are not resolving",
                "this usually fixes itself; if not, the network's DNS is down",
                True),
            NetworkFault.CAPTIVE_PORTAL: (
                "this network wants you to sign in first",
                "open a browser and complete the network's login page", True),
            NetworkFault.TLS: (
                "the secure connection was refused",
                "check this device's date and time - a clock that is wrong "
                "breaks every secure connection", True),
            NetworkFault.SERVER: (
                "the server refused the request",
                "nothing to do here; it is not this device", True),
            NetworkFault.METERED: (
                "this connection costs money",
                "wait for Wi-Fi, or say to go ahead anyway", False),
            NetworkFault.NO_SPACE: (
                "there is not enough room on this device",
                "free some space and try again", False),
            NetworkFault.TIMEOUT: (
                "the connection was too slow to finish",
                "try again on a faster connection", True),
        }
        if fault is NetworkFault.NONE:
            return NetworkDiagnosis.healthy()
        message, suggestion, retry = table[fault]
        return NetworkDiagnosis(fault, message, suggestion, retry)


class INetworkPreflight(ABC):
    """Checks whether a fetch can work before starting one."""

    @abstractmethod
    def check(self, url: str = "") -> NetworkDiagnosis: ...


class NetworkPreflight(INetworkPreflight):
    """The default preflight.

    ORDER MATTERS and it is cheapest-first: no link, then a name, then a
    handshake. Probing TLS on a device with no link wastes a timeout to learn
    what one flag already said.
    """

    def __init__(
        self,
        conditions: Callable[[], NetworkConditions] | None = None,
        resolve: Callable[[str], bool] | None = None,
        probe: Callable[[str], int] | None = None,
        free_bytes: Callable[[], int] | None = None,
        required_bytes: int = 0,
    ) -> None:
        self._conditions = conditions
        self._resolve = resolve
        self._probe = probe
        self._free_bytes = free_bytes
        self._required_bytes = required_bytes

    def check(self, url: str = "") -> NetworkDiagnosis:
        conditions = self._conditions() if self._conditions else NetworkConditions()
        if not conditions.is_connected:
            return NetworkDiagnosis.of(NetworkFault.OFFLINE)

        if self._required_bytes > 0 and self._free_bytes is not None:
            # Space is checked BEFORE the network. Spending a gigabyte of
            # somebody's bundle and then failing to write it is the worst
            # possible order for these two checks.
            if self._free_bytes() < self._required_bytes:
                return NetworkDiagnosis.of(NetworkFault.NO_SPACE)

        host = ""
        if url:
            match = re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*://([^/:?#]+)", url)
            host = match.group(1) if match else ""
        if host and self._resolve is not None and not self._resolve(host):
            return NetworkDiagnosis.of(NetworkFault.DNS)

        if url and self._probe is not None:
            try:
                status = self._probe(url)
            except TimeoutError:
                return NetworkDiagnosis.of(NetworkFault.TIMEOUT)
            except Exception as exc:  # noqa: BLE001
                text = str(exc).lower()
                # A certificate error is reported as TLS, not as a generic
                # failure, because the fix is almost always the device clock and
                # nobody guesses that.
                if "certificate" in text or "ssl" in text or "tls" in text:
                    return NetworkDiagnosis.of(NetworkFault.TLS)
                return NetworkDiagnosis.of(NetworkFault.SERVER)
            # A redirect to a login page is how a captive portal answers, and it
            # is indistinguishable from success unless it is looked for.
            if status in (204, 200):
                return NetworkDiagnosis.healthy()
            if status in (301, 302, 303, 307, 511):
                return NetworkDiagnosis.of(NetworkFault.CAPTIVE_PORTAL)
            if status >= 500:
                return NetworkDiagnosis.of(NetworkFault.SERVER)
            return NetworkDiagnosis.of(NetworkFault.SERVER)

        if not conditions.is_known_unmetered:
            return NetworkDiagnosis.of(NetworkFault.METERED)
        return NetworkDiagnosis.healthy()


# ─────────────────────────────────────────────────────────────────────────────
# Native runtime


@dataclass(frozen=True)
class NativeRuntimePaths:
    """Where the native pieces landed."""

    library_directory: str = ""
    resolved_library: str = ""
    abi: str = ""
    searched: tuple[str, ...] = ()


class NativeLibraryResolver:
    """Finds the native library for THIS device's ABI.

    Android packs one directory per ABI and a device may run more than one -
    an arm64 phone happily runs armeabi-v7a. Picking the first that exists
    rather than the first the device PREFERS silently runs 32-bit code on a
    64-bit phone, which works, is slower, and is invisible.
    """

    #: Most capable first. The order is the answer, not the contents.
    ANDROID_ABIS = ("arm64-v8a", "armeabi-v7a", "x86_64", "x86")
    APPLE_ABIS = ("arm64", "x86_64")

    def __init__(
        self,
        supported_abis: Sequence[str] = (),
        exists: Callable[[str], bool] | None = None,
    ) -> None:
        self._supported = tuple(supported_abis)
        self._exists = exists or os.path.exists

    def preferred_abis(self) -> tuple[str, ...]:
        """The device's list, filtered to what we know, in OUR order.

        The device's own ordering is trusted where it exists; where it does not,
        the fallback is most-capable-first.
        """
        if self._supported:
            known = [a for a in self._supported if a in self.ANDROID_ABIS + self.APPLE_ABIS]
            if known:
                return tuple(known)
        return self.ANDROID_ABIS

    def resolve(self, root: str, library_name: str) -> NativeRuntimePaths:
        searched: list[str] = []
        for abi in self.preferred_abis():
            candidate = os.path.join(root, abi, library_name)
            searched.append(candidate)
            if self._exists(candidate):
                return NativeRuntimePaths(
                    os.path.join(root, abi), candidate, abi, tuple(searched))
        flat = os.path.join(root, library_name)
        searched.append(flat)
        if self._exists(flat):
            return NativeRuntimePaths(root, flat, "", tuple(searched))
        # Returns the SEARCHED LIST rather than raising. "libmnn.so not found"
        # is unactionable; the list of places looked in is a diagnosis.
        return NativeRuntimePaths(searched=tuple(searched))


@dataclass(frozen=True)
class MnnRuntimeConfig:
    """How the native runtime is set up."""

    thread_count: int = 4
    #: Big cores only by default. Spreading inference across little cores makes
    #: it slower AND hotter: the little cores finish late and the big ones idle
    #: waiting on the barrier.
    big_cores_only: bool = True
    use_gpu: bool = False
    use_fp16: bool = True
    memory_mode: str = "low"

    @staticmethod
    def for_threads(available: int) -> "MnnRuntimeConfig":
        """Half the cores, at least one, at most six.

        Never all of them: a runtime that takes every core makes the UI thread
        wait, and an assistant that freezes the phone while it thinks is worse
        than one that thinks a little slower.
        """
        return MnnRuntimeConfig(thread_count=max(1, min(6, available // 2)))


class MnnNativeDiagnostics:
    """What the native side reports about itself.

    Every field OPTIONAL and absent when unknown. A diagnostics screen that
    invents a zero where it has no measurement is worse than one that says it
    does not know, because a zero looks like a finding.
    """

    def __init__(self) -> None:
        self._values: dict[str, object] = {}

    def record(self, key: str, value: object) -> None:
        self._values[key] = value

    def get(self, key: str) -> object | None:
        return self._values.get(key)

    def snapshot(self) -> dict[str, object]:
        return dict(self._values)

    def describe(self) -> str:
        if not self._values:
            return "the native runtime has not reported anything"
        return ", ".join(f"{k}={v}" for k, v in sorted(self._values.items()))


class NativeRuntimePrep:
    """Gets the native side ready, once.

    IDEMPOTENT. Preparation runs from whichever call arrives first, and on a
    phone that is often two at the same time - a warm-up and a real request
    racing on separate threads.
    """

    def __init__(
        self,
        resolver: NativeLibraryResolver | None = None,
        diagnostics: MnnNativeDiagnostics | None = None,
    ) -> None:
        self._resolver = resolver or NativeLibraryResolver()
        self._diagnostics = diagnostics or MnnNativeDiagnostics()
        self._paths: NativeRuntimePaths | None = None
        self._prepared = False

    @property
    def is_prepared(self) -> bool:
        return self._prepared

    @property
    def paths(self) -> NativeRuntimePaths | None:
        return self._paths

    @property
    def diagnostics(self) -> MnnNativeDiagnostics:
        return self._diagnostics

    def prepare(self, root: str, library_name: str) -> bool:
        if self._prepared:
            return True
        paths = self._resolver.resolve(root, library_name)
        self._paths = paths
        self._diagnostics.record("abi", paths.abi or "unresolved")
        self._diagnostics.record("searched", len(paths.searched))
        if not paths.resolved_library:
            self._diagnostics.record("error", "no native library for this device")
            return False
        self._diagnostics.record("library", paths.resolved_library)
        self._prepared = True
        return True


class MmapWeightLoader:
    """Maps weights instead of reading them.

    A 4 GB model READ into a phone's heap is a 4 GB allocation the system will
    refuse or kill. Mapped, the pages come in on demand and the kernel evicts
    them under pressure - which is the difference between a model that runs on a
    6 GB phone and one that does not.

    The map is not made here; a host supplies it. What is here is the arithmetic
    that decides whether it is worth attempting.
    """

    def __init__(self, map_file: Callable[[str, int, int], memoryview] | None = None) -> None:
        self._map = map_file

    @staticmethod
    def should_map(file_bytes: int, available_ram_bytes: int) -> bool:
        """Map when the file is more than a QUARTER of memory.

        Not half: the model is not the only thing running, and by the time a
        file is half of RAM the allocation has already failed.
        """
        if file_bytes <= 0 or available_ram_bytes <= 0:
            return False
        return file_bytes * 4 > available_ram_bytes

    def load(self, path: str, offset: int = 0, length: int = 0) -> memoryview | None:
        if self._map is None:
            return None
        size = length or (os.path.getsize(path) - offset if os.path.exists(path) else 0)
        if size <= 0:
            return None
        return self._map(path, offset, size)


# ─────────────────────────────────────────────────────────────────────────────
# Offload


@dataclass(frozen=True)
class MeshPeer:
    """Another device that could do the work."""

    peer_id: str
    display_name: str = ""
    #: Whether BOTH devices added each other. Offloading to a peer that has not
    #: added us back is sending a prompt to a stranger.
    mutually_added: bool = False
    ram_bytes: int = 0
    #: Measured, not advertised. A peer's own claim about its speed is a claim.
    measured_tokens_per_second: float = 0.0
    load_average: float = 0.0


class MeshOffloadStrategy(Enum):
    """When work may leave this device."""

    #: The default. Nothing leaves.
    NEVER = "never"
    #: Only when this device genuinely cannot do it at all.
    ONLY_IF_INCAPABLE = "only-if-incapable"
    #: When a peer is meaningfully faster AND the person agreed to that peer.
    PREFER_FASTER_PEER = "prefer-faster-peer"


@dataclass(frozen=True)
class OffloadVerdict:
    """Whether to offload, and why.

    The REASON is mandatory. An offload decision without a reason is a decision
    nobody can review, and this one moves somebody's words to another machine.
    """

    should_offload: bool
    reason: str
    peer: MeshPeer | None = None

    @staticmethod
    def stay(reason: str) -> "OffloadVerdict":
        return OffloadVerdict(False, reason)

    @staticmethod
    def go(peer: MeshPeer, reason: str) -> "OffloadVerdict":
        return OffloadVerdict(True, reason, peer)


class MeshOffloadPlanner:
    """Applies the strategy."""

    #: A peer must be this much faster before moving work is worth it. Below
    #: this the transfer costs more than it saves, and it has also told another
    #: device what was asked.
    SPEEDUP_THRESHOLD = 1.5

    def __init__(
        self,
        strategy: MeshOffloadStrategy = MeshOffloadStrategy.NEVER,
        consented_peer_ids: Sequence[str] = (),
    ) -> None:
        self._strategy = strategy
        self._consented = {p.strip() for p in consented_peer_ids if p.strip()}

    def decide(
        self, peers: Sequence[MeshPeer], local_tokens_per_second: float,
        can_run_locally: bool = True,
    ) -> OffloadVerdict:
        if self._strategy is MeshOffloadStrategy.NEVER:
            return OffloadVerdict.stay("this device never sends work elsewhere")

        # Consent, then mutual, then capability - in that order, so a peer that
        # fails the first test is never evaluated on speed.
        eligible = [
            p for p in peers
            if p.peer_id in self._consented and p.mutually_added
        ]
        if not eligible:
            return OffloadVerdict.stay(
                "no peer has both been agreed to and added this device back")

        if can_run_locally and self._strategy is MeshOffloadStrategy.ONLY_IF_INCAPABLE:
            return OffloadVerdict.stay("this device can do it, so it will")

        best = max(eligible, key=lambda p: p.measured_tokens_per_second)
        if best.measured_tokens_per_second <= 0:
            return OffloadVerdict.stay("no peer has been measured, only claimed")
        if not can_run_locally:
            return OffloadVerdict.go(
                best, f"this device cannot run it; {best.peer_id} can, and you agreed")
        if local_tokens_per_second <= 0:
            return OffloadVerdict.stay("this device's own speed is unmeasured")
        speedup = best.measured_tokens_per_second / local_tokens_per_second
        if speedup < self.SPEEDUP_THRESHOLD:
            return OffloadVerdict.stay(
                f"{best.peer_id} is only {speedup:.1f}x faster, which is not "
                f"worth sending your words to another device")
        return OffloadVerdict.go(
            best, f"{best.peer_id} is {speedup:.1f}x faster, and you agreed to it")


class SpeculativeDecodingPipeline:
    """A small model drafts, the big one checks.

    THE ACCEPTED PREFIX IS WHAT THE BIG MODEL WOULD HAVE PRODUCED ANYWAY, so
    this is a speed change and not a quality one. That is the whole claim, and
    it holds only if the check is exact: the moment a draft token is accepted
    without the target agreeing, the output is the small model's and the claim
    is false.

    On the first disagreement the target's own token is taken and the rest of
    the draft is DISCARDED - keeping any of it would be keeping tokens
    conditioned on a prefix that did not happen.
    """

    def __init__(self, draft_length: int = 4) -> None:
        #: Longer drafts win more when they are right and cost more when they
        #: are wrong. Four is the point where the two roughly balance on a
        #: phone.
        self._draft_length = max(1, draft_length)

    @property
    def draft_length(self) -> int:
        return self._draft_length

    def accept(
        self, draft_tokens: Sequence[int], target_tokens: Sequence[int]
    ) -> tuple[list[int], int]:
        """Returns (accepted, rejected_at).

        `rejected_at` is the index of the first disagreement, or the draft
        length when all of it was accepted.
        """
        accepted: list[int] = []
        for i, token in enumerate(draft_tokens):
            if i >= len(target_tokens) or target_tokens[i] != token:
                return accepted, i
            accepted.append(token)
        return accepted, len(draft_tokens)

    def step(
        self, draft: Callable[[int], list[int]],
        verify: Callable[[Sequence[int]], list[int]],
    ) -> list[int]:
        """One round. Always emits at least ONE token - the target's own at the
        point of disagreement - so the loop cannot stall on a draft that is
        always wrong."""
        drafted = draft(self._draft_length)
        checked = verify(drafted)
        accepted, at = self.accept(drafted, checked)
        if at < len(drafted) and at < len(checked):
            accepted.append(checked[at])
        elif not accepted and checked:
            accepted.append(checked[0])
        return accepted


# ─────────────────────────────────────────────────────────────────────────────
# Bundles


class SideloadOutcome(Enum):
    """How importing a handed-over model went."""

    IMPORTED = "imported"
    #: Already present and identical. NOT an error - handing somebody a model
    #: they already have is the normal case in a room full of phones.
    ALREADY_PRESENT = "already-present"
    #: The bytes do not match the digest. The only correct response is refusal.
    DIGEST_MISMATCH = "digest-mismatch"
    UNSUPPORTED_FORMAT = "unsupported-format"
    NO_SPACE = "no-space"
    REFUSED = "refused"


@dataclass(frozen=True)
class SideloadResult:
    """What the import did."""

    outcome: SideloadOutcome
    model_id: str = ""
    installed_path: str = ""
    bytes_written: int = 0
    message: str = ""

    @property
    def succeeded(self) -> bool:
        return self.outcome in (
            SideloadOutcome.IMPORTED, SideloadOutcome.ALREADY_PRESENT)


class SideloadedBundleImporter:
    """Imports a model handed over offline.

    THIS IS THE POINT OF THE WHOLE DESIGN: a model arrives on a phone from
    another phone, over Wi-Fi Direct, with no internet involved. Which is also
    why the digest check is not optional - a file from a peer is a file from
    somebody else's device, and a model that has been altered is a model that
    says what somebody else wanted it to say.
    """

    SUPPORTED_SUFFIXES = (".onnx", ".gguf", ".mnn", ".bin", ".safetensors")

    def __init__(
        self,
        install_root: str = "",
        free_bytes: Callable[[], int] | None = None,
        read_file: Callable[[str], bytes] | None = None,
        write_file: Callable[[str, bytes], None] | None = None,
        exists: Callable[[str], bool] | None = None,
    ) -> None:
        self._root = install_root
        self._free_bytes = free_bytes
        self._read = read_file
        self._write = write_file
        self._exists = exists or (lambda p: False)

    @staticmethod
    def digest(data: bytes) -> str:
        return hashlib.sha256(data).hexdigest()

    def import_bundle(
        self, model_id: str, source_path: str, expected_sha256: str,
    ) -> SideloadResult:
        if not expected_sha256:
            # No digest means no import. Accepting a file on trust because the
            # sender did not supply one is exactly the case this refuses.
            return SideloadResult(
                SideloadOutcome.REFUSED, model_id,
                message="this file came with no checksum, so it cannot be trusted")
        if not any(source_path.lower().endswith(s) for s in self.SUPPORTED_SUFFIXES):
            return SideloadResult(
                SideloadOutcome.UNSUPPORTED_FORMAT, model_id,
                message=f"{os.path.basename(source_path)} is not a model file "
                        f"this build can load")
        if self._read is None:
            return SideloadResult(
                SideloadOutcome.REFUSED, model_id, message="no way to read the file")

        data = self._read(source_path)
        actual = self.digest(data)
        if not _constant_time_equals(actual, expected_sha256.strip().lower()):
            return SideloadResult(
                SideloadOutcome.DIGEST_MISMATCH, model_id,
                message="this file does not match its checksum and was not installed")

        target = os.path.join(self._root, model_id, os.path.basename(source_path))
        if self._exists(target):
            return SideloadResult(
                SideloadOutcome.ALREADY_PRESENT, model_id, target, len(data),
                "this device already has that model")
        if self._free_bytes is not None and self._free_bytes() < len(data):
            return SideloadResult(
                SideloadOutcome.NO_SPACE, model_id,
                message=f"this needs "
                        f"{MeteredNetworkDownloadGate.describe_size(len(data))} "
                        f"and there is not that much room")
        if self._write is None:
            return SideloadResult(
                SideloadOutcome.REFUSED, model_id, message="no way to write the file")
        self._write(target, data)
        return SideloadResult(
            SideloadOutcome.IMPORTED, model_id, target, len(data),
            "installed from a file on this device")


def _constant_time_equals(a: str, b: str) -> bool:
    """Compares without leaking WHERE two digests differ.

    Overkill for a local file and correct anyway: the habit of comparing digests
    in constant time is the thing worth keeping, because the one place it is
    skipped is always the place it mattered.
    """
    if len(a) != len(b):
        return False
    difference = 0
    for x, y in zip(a, b):
        difference |= ord(x) ^ ord(y)
    return difference == 0


class BundleModelLoader:
    """Loads a model from an installed bundle.

    Every file is checked against the manifest BEFORE anything is loaded. A
    partial bundle that loads two of three files produces a model that runs and
    is wrong, which is worse than one that does not run.
    """

    def __init__(
        self,
        exists: Callable[[str], bool] | None = None,
        size_of: Callable[[str], int] | None = None,
    ) -> None:
        self._exists = exists or os.path.exists
        self._size_of = size_of or (lambda p: 0)

    def verify(
        self, root: str, expected: dict[str, int]
    ) -> tuple[bool, list[str]]:
        """Returns (complete, problems). Reports EVERY problem, not the first.

        Fixing one missing file, re-running, and finding the next is three trips
        where one would do.
        """
        problems: list[str] = []
        for name, size in sorted(expected.items()):
            path = os.path.join(root, name)
            if not self._exists(path):
                problems.append(f"{name} is missing")
            elif size > 0 and self._size_of(path) != size:
                problems.append(
                    f"{name} is {self._size_of(path)} bytes, expected {size}")
        return not problems, problems


# ─────────────────────────────────────────────────────────────────────────────
# Selection


class SelectionQuality(IntEnum):
    """How good a match a selected model is.

    ORDERED, so a caller can compare. The order is the meaning: a caller decides
    whether to proceed by asking whether the quality is at least something.
    """

    #: Nothing suitable. Not an error - a device that cannot do a thing should
    #: say so rather than doing it badly.
    NONE = 0
    #: It will run and it will be poor. Offered only when the caller asked for
    #: anything at all.
    DEGRADED = 1
    ACCEPTABLE = 2
    GOOD = 3
    #: Exactly what was asked for, on hardware that fits it.
    IDEAL = 4


class Resolution(Enum):
    """What a power budget resolved to for one call.

    Separate from the budget itself because the budget is a REQUEST and this is
    what the device decided - and on a hot phone at 8% battery those are not the
    same thing.
    """

    HONOURED = "honoured"
    #: Lowered because of battery, heat or a foreground app. The caller is told,
    #: so a shorter answer is explained rather than mysterious.
    THROTTLED = "throttled"
    #: Raised because the device is charging and cool.
    RELAXED = "relaxed"
    #: Refused outright. The device will not spend the power at all.
    DECLINED = "declined"


@dataclass(frozen=True)
class ModalityPlan:
    """Which engine handles which part of a request.

    A request can need several - speech in, text through, speech out - and each
    may come from a different place. Held together so a caller can be told the
    whole route before any of it runs.
    """

    transcribe_with: str = ""
    generate_with: str = ""
    speak_with: str = ""
    see_with: str = ""
    quality: SelectionQuality = SelectionQuality.NONE
    #: Why this plan. Shown when a person asks why the assistant sounds
    #: different today.
    reason: str = ""

    @property
    def is_complete(self) -> bool:
        return bool(self.generate_with) and self.quality > SelectionQuality.NONE


class ISpeechModelSelector(ABC):
    """Picks the speech models for a request."""

    @abstractmethod
    def plan(
        self, language: str, needs_speech_in: bool, needs_speech_out: bool
    ) -> ModalityPlan: ...


class SpeechModelSelector(ISpeechModelSelector):
    """The default selector.

    NO MODEL NAME IS HARDCODED. The catalogue supplies them, keyed by language,
    because a hardcoded name is a model that cannot be replaced without a
    release - and the catalogue is exactly where a device learns it now has a
    better voice for a language it used to handle badly.
    """

    def __init__(
        self,
        transcribers_by_language: dict[str, str] | None = None,
        voices_by_language: dict[str, str] | None = None,
        generator_id: str = "",
    ) -> None:
        self._transcribers = {k.lower(): v for k, v in (transcribers_by_language or {}).items()}
        self._voices = {k.lower(): v for k, v in (voices_by_language or {}).items()}
        self._generator = generator_id

    @staticmethod
    def _base_language(tag: str) -> str:
        """`af-ZA` and `af` are the same language for choosing a model.

        Falling back to the base tag is what makes a device with one Afrikaans
        voice usable by somebody whose locale says af-NA.
        """
        return tag.split("-")[0].split("_")[0].lower()

    def plan(
        self, language: str, needs_speech_in: bool, needs_speech_out: bool
    ) -> ModalityPlan:
        tag = language.strip().lower()
        base = self._base_language(tag)

        def look_up(table: dict[str, str]) -> str:
            return table.get(tag) or table.get(base, "")

        transcribe = look_up(self._transcribers) if needs_speech_in else ""
        speak = look_up(self._voices) if needs_speech_out else ""

        if not self._generator:
            return ModalityPlan(
                transcribe, "", speak, quality=SelectionQuality.NONE,
                reason="this device has no text model, so it cannot answer at all")

        wanted = (1 if needs_speech_in else 0) + (1 if needs_speech_out else 0)
        got = (1 if transcribe else 0) + (1 if speak else 0)
        if wanted == 0:
            quality, reason = SelectionQuality.IDEAL, "text only, which this device does"
        elif got == wanted:
            quality = (
                SelectionQuality.IDEAL if look_up(self._voices) or not needs_speech_out
                else SelectionQuality.GOOD)
            reason = f"this device has everything needed for {language}"
        elif got == 0:
            quality, reason = SelectionQuality.DEGRADED, (
                f"this device has no speech models for {language}, so it will "
                f"answer in text")
        else:
            quality, reason = SelectionQuality.ACCEPTABLE, (
                f"this device has some of what {language} needs, but not all")
        return ModalityPlan(transcribe, self._generator, speak, quality=quality, reason=reason)


# ─────────────────────────────────────────────────────────────────────────────
# Generators


class ManagedTextGeneratorBase:
    """What the model-family generators share.

    They differ in their PROMPT FORMAT and nothing else that matters here.
    Getting the format wrong does not fail - it produces a model that answers
    slightly worse and nobody can say why, which is why each one is written out
    rather than approximated by a shared template.
    """

    def __init__(self, model_id: str = "", generate: Callable[[str], str] | None = None) -> None:
        self._model_id = model_id
        self._generate = generate

    @property
    def model_id(self) -> str:
        return self._model_id

    @property
    def is_available(self) -> bool:
        return bool(self._model_id) and self._generate is not None

    def format_prompt(self, turns: Sequence[tuple[str, str]], system: str = "") -> str:
        raise NotImplementedError

    def generate(self, turns: Sequence[tuple[str, str]], system: str = "") -> str:
        if not self.is_available:
            raise RuntimeError(f"{type(self).__name__} has no model loaded")
        return self._generate(self.format_prompt(turns, system))


class QwenTextGenerator(ManagedTextGeneratorBase):
    """Qwen's ChatML format."""

    def format_prompt(self, turns: Sequence[tuple[str, str]], system: str = "") -> str:
        parts: list[str] = []
        if system:
            parts.append(f"<|im_start|>system\n{system}<|im_end|>")
        for role, content in turns:
            parts.append(f"<|im_start|>{role}\n{content}<|im_end|>")
        # The trailing OPEN tag is what tells the model it is its turn. Leaving
        # it off makes the model continue the conversation as the user, which
        # reads as the assistant talking to itself.
        parts.append("<|im_start|>assistant\n")
        return "\n".join(parts)


class KimiVlGenerator(ManagedTextGeneratorBase):
    """Kimi's vision-language format.

    Images are referenced by a PLACEHOLDER in the text, in order. The order is
    the binding - an image list that does not match the placeholders describes
    the wrong picture with complete confidence.
    """

    IMAGE_TOKEN = "<|media_start|>image<|media_content|><|media_end|>"

    def __init__(
        self, model_id: str = "",
        generate: Callable[[str], str] | None = None,
        image_count: int = 0,
    ) -> None:
        super().__init__(model_id, generate)
        self._image_count = max(0, image_count)

    @property
    def image_count(self) -> int:
        return self._image_count

    def format_prompt(self, turns: Sequence[tuple[str, str]], system: str = "") -> str:
        parts: list[str] = []
        if system:
            parts.append(f"<|im_system|>system<|im_middle|>{system}<|im_end|>")
        for i, (role, content) in enumerate(turns):
            prefix = self.IMAGE_TOKEN if i == 0 and self._image_count else ""
            parts.append(f"<|im_user|>{role}<|im_middle|>{prefix}{content}<|im_end|>")
        parts.append("<|im_assistant|>assistant<|im_middle|>")
        return "".join(parts)
