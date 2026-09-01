"""What the device is, where things live, and what has actually been verified.

THE RAM MEASUREMENT IS THE REASON THIS FILE EXISTS. A managed runtime will
happily tell you about its own heap when you ask how much memory the device has,
and the number looks plausible - a few hundred megabytes, growing under load. It
is not the device's memory. A model chooser fed that number picks a model to fit
a heap rather than a phone, and the result is either a device that refuses a
model it could run or one that is killed loading a model it could not.

So a measurement here carries WHERE IT CAME FROM, and a heap reading is marked
as a heap reading rather than being quietly used as physical memory.

THE SECOND THING HERE IS HONESTY ABOUT VERIFICATION. "It compiles" and "it ran
on the phone" are different claims, and only the second one is worth anything.
The attribute in this file makes the difference recordable rather than a matter
of memory.
"""

from __future__ import annotations

import os
import posixpath
import re
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum, IntEnum
from typing import Callable, Sequence


# ─────────────────────────────────────────────────────────────────────────────
# Memory


class PlatformMemory(Enum):
    """Where a memory number came from.

    THE POINT OF THE WHOLE TYPE. A managed runtime's heap figure and the
    device's physical memory are both "a number of bytes about memory", and
    using one where the other was meant is invisible until a model gets killed
    on a phone that had plenty of room.
    """

    #: Nothing measured. Not zero - unknown. A chooser must treat this as "do
    #: not know" and refuse to size anything by it.
    UNKNOWN = "unknown"
    #: Read from the operating system: /proc/meminfo, ActivityManager, sysctl.
    #: The only source that answers the question actually being asked.
    PHYSICAL = "physical"
    #: The managed runtime's own heap. NEVER the device's memory, and named so
    #: that using it as such requires writing the word.
    MANAGED_HEAP = "managed-heap"
    #: What a container or cgroup will allow, which on a server is the real
    #: ceiling regardless of what the host machine has.
    CGROUP_LIMIT = "cgroup-limit"


@dataclass(frozen=True)
class RamMeasurement:
    """How much memory, and how we know.

    A measurement with no source is refused at construction: an unsourced number
    is exactly the bug this type exists to prevent, and letting one be built
    would put it back.
    """

    total_bytes: int = 0
    available_bytes: int = 0
    source: PlatformMemory = PlatformMemory.UNKNOWN

    def __post_init__(self) -> None:
        if self.total_bytes and self.source is PlatformMemory.UNKNOWN:
            raise ValueError(
                "a memory measurement with a value must say where it came from")

    @property
    def is_usable_for_sizing(self) -> bool:
        """Whether a model chooser may size anything by this.

        A HEAP READING IS NOT USABLE, and that is the rule the whole file is
        built around: it describes the runtime's allocations, not the phone.
        """
        return (
            self.total_bytes > 0
            and self.source in (PlatformMemory.PHYSICAL, PlatformMemory.CGROUP_LIMIT)
        )

    @property
    def total_gb(self) -> float:
        return self.total_bytes / float(1 << 30)

    def describe(self) -> str:
        if self.source is PlatformMemory.UNKNOWN or not self.total_bytes:
            return "this device's memory has not been measured"
        if not self.is_usable_for_sizing:
            return (
                f"{self.total_gb:.1f} GB of managed heap - this is NOT the "
                f"device's memory and must not be used to choose a model")
        return f"{self.total_gb:.1f} GB of memory"

    @staticmethod
    def unknown() -> "RamMeasurement":
        return RamMeasurement()

    @staticmethod
    def physical(total: int, available: int = 0) -> "RamMeasurement":
        return RamMeasurement(total, available or total, PlatformMemory.PHYSICAL)

    @staticmethod
    def managed_heap(total: int) -> "RamMeasurement":
        """Deliberately awkward to build and clearly named at every call site."""
        return RamMeasurement(total, 0, PlatformMemory.MANAGED_HEAP)


class PlatformInterop:
    """The seam to whatever the host can actually ask.

    Every probe is a callable the host supplies, and every one of them may be
    absent. A build with none of them returns UNKNOWN everywhere - which is
    correct, and better than a plausible number nobody can trace.
    """

    def __init__(
        self,
        physical_memory: Callable[[], tuple[int, int]] | None = None,
        cgroup_limit: Callable[[], int] | None = None,
        cpu_count: Callable[[], int] | None = None,
        battery_percent: Callable[[], int] | None = None,
        is_charging: Callable[[], bool] | None = None,
        thermal_status: Callable[[], str] | None = None,
        free_storage: Callable[[str], int] | None = None,
    ) -> None:
        self._physical_memory = physical_memory
        self._cgroup_limit = cgroup_limit
        self._cpu_count = cpu_count
        self._battery_percent = battery_percent
        self._is_charging = is_charging
        self._thermal_status = thermal_status
        self._free_storage = free_storage

    def measure_ram(self) -> RamMeasurement:
        """A cgroup limit BEATS the physical figure where both exist.

        On a container the host's memory is not what this process may use, and
        sizing a model by the host's figure gets the process killed by the
        thing that set the limit.
        """
        if self._cgroup_limit is not None:
            limit = self._cgroup_limit()
            if limit > 0:
                return RamMeasurement(limit, limit, PlatformMemory.CGROUP_LIMIT)
        if self._physical_memory is not None:
            total, available = self._physical_memory()
            if total > 0:
                return RamMeasurement.physical(total, available)
        return RamMeasurement.unknown()

    def cpu_count(self) -> int:
        """At least 1. A zero here divides by zero in every thread calculation
        downstream."""
        if self._cpu_count is None:
            return max(1, os.cpu_count() or 1)
        return max(1, self._cpu_count())

    def battery_percent(self) -> int | None:
        """None when unknown. NOT 100 - a device that assumes a full battery
        because it cannot read one will spend a flat phone's last minutes on
        inference."""
        return self._battery_percent() if self._battery_percent else None

    def is_charging(self) -> bool | None:
        return self._is_charging() if self._is_charging else None

    def thermal_status(self) -> str:
        return self._thermal_status() if self._thermal_status else "unknown"

    def free_storage(self, path: str = ".") -> int:
        return self._free_storage(path) if self._free_storage else 0


@dataclass(frozen=True)
class SystemInfoDeviceContext:
    """What the assistant knows about the hardware it is on."""

    device_name: str = ""
    platform: str = ""
    ram: RamMeasurement = field(default_factory=RamMeasurement.unknown)
    cpu_count: int = 1
    battery_percent: int | None = None
    is_charging: bool | None = None
    thermal_status: str = "unknown"
    free_storage_bytes: int = 0

    @staticmethod
    def probe(interop: PlatformInterop, device_name: str = "", platform: str = "") -> "SystemInfoDeviceContext":
        return SystemInfoDeviceContext(
            device_name, platform, interop.measure_ram(), interop.cpu_count(),
            interop.battery_percent(), interop.is_charging(),
            interop.thermal_status(), interop.free_storage(),
        )

    @property
    def can_size_models(self) -> bool:
        """Whether anything may be chosen by this device's memory figure."""
        return self.ram.is_usable_for_sizing

    def describe(self) -> str:
        parts = [self.device_name or "this device", self.ram.describe(),
                 f"{self.cpu_count} cores"]
        if self.battery_percent is not None:
            state = "charging" if self.is_charging else "on battery"
            parts.append(f"{self.battery_percent}% {state}")
        return ", ".join(parts)


# ─────────────────────────────────────────────────────────────────────────────
# Models on disk


class ModelModality(Enum):
    """What a model does."""

    TEXT = "text"
    #: Speech to text.
    TRANSCRIPTION = "transcription"
    #: Text to speech.
    SPEECH = "speech"
    VISION = "vision"
    EMBEDDING = "embedding"
    #: Text and vision together. Its own value rather than a pair, because a
    #: model that takes both is not the same as two that each take one.
    MULTIMODAL = "multimodal"
    RERANK = "rerank"


class DownloadPhase(Enum):
    """Where a download has got to.

    VERIFYING and INSTALLING are separate from DOWNLOADING because they are what
    a person is waiting through after the progress bar reaches the end - and a
    bar that sits at 100% with no explanation reads as a hang.
    """

    IDLE = "idle"
    #: Working out what to fetch. Fast, and worth naming so the UI does not show
    #: 0% during it.
    RESOLVING = "resolving"
    DOWNLOADING = "downloading"
    #: Checking the digest. On a phone, hashing four gigabytes is a real wait.
    VERIFYING = "verifying"
    INSTALLING = "installing"
    COMPLETE = "complete"
    FAILED = "failed"
    #: Stopped on purpose. NOT a failure, and shown differently.
    CANCELLED = "cancelled"


@dataclass(frozen=True)
class ModelSource:
    """Where a model comes from.

    NO MODEL NAME AND NO DEFAULT REPOSITORY. Both are supplied by the
    catalogue, because a hardcoded either is a thing that cannot be changed
    without a release.
    """

    source_id: str = ""
    repository: str = ""
    revision: str = ""
    files: tuple[str, ...] = ()
    #: Keyed by file name. A file with no digest is refused on import, so this
    #: being complete is the difference between a bundle that can be verified
    #: and one that cannot.
    digests: dict[str, str] = field(default_factory=dict)
    total_bytes: int = 0

    @property
    def is_verifiable(self) -> bool:
        return bool(self.files) and all(self.digests.get(f) for f in self.files)


class ModelPaths:
    """Where model files live on this device.

    EVERY PATH IS CONTAINED. A model id arrives from a catalogue, which is
    fetched, which means it is input - and an id of `../../../etc` that joins
    cleanly writes outside the model directory. The containment check here is
    the only thing between a catalogue entry and an arbitrary file write.
    """

    #: Deliberately strict. A model id is an identifier, not a path, and
    #: anything a filesystem could interpret is rejected rather than escaped.
    _SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

    def __init__(self, root: str) -> None:
        if not root:
            raise ValueError("a model root is required")
        self._root = os.path.abspath(root)

    @property
    def root(self) -> str:
        return self._root

    @classmethod
    def is_safe_id(cls, model_id: str) -> bool:
        """A single segment only. A slash makes it a path, and a path is the
        thing being defended against - so `org/model` is normalised by the
        caller before it reaches here, never accepted."""
        return bool(cls._SAFE_ID.match(model_id or ""))

    def model_directory(self, model_id: str) -> str:
        if not self.is_safe_id(model_id):
            raise ValueError(f"{model_id!r} is not a usable model identifier")
        return os.path.join(self._root, model_id)

    def file_path(self, model_id: str, file_name: str) -> str:
        """Contained by comparing RESOLVED paths, not by inspecting the string.

        Checking for ".." in the text misses a symlink, a case-folded duplicate
        on a case-insensitive filesystem, and an absolute path that overrides
        the join entirely. Resolving both sides and comparing prefixes catches
        all three, because it asks the filesystem the question rather than
        guessing the answer.
        """
        directory = self.model_directory(model_id)
        candidate = os.path.abspath(os.path.join(directory, file_name))
        contained = os.path.join(os.path.abspath(directory), "")
        if not candidate.startswith(contained):
            raise ValueError(
                f"{file_name!r} would write outside this model's directory")
        return candidate

    def manifest_path(self, model_id: str) -> str:
        return self.file_path(model_id, "manifest.json")

    @staticmethod
    def normalise_id(repository: str) -> str:
        """Turns `org/model` into a single safe segment.

        The separator becomes `--`, which is reversible by eye and cannot be a
        directory boundary. Lower-cased so a case-insensitive filesystem cannot
        hold two directories that a case-sensitive one would keep apart - which
        is how the same model gets downloaded twice on one platform and once on
        another.
        """
        cleaned = re.sub(r"[^A-Za-z0-9._/-]", "-", repository.strip())
        return cleaned.replace("/", "--").strip("-.").lower()[:128]


class EmbeddedVoiceConfigs:
    """Voice configuration that ships INSIDE the app.

    Not because a voice is embedded - the voices are downloaded - but because
    the shape of each family's configuration is code-adjacent knowledge that
    must be right before any file arrives. A device that downloads a voice and
    then cannot work out its sample rate has a voice it cannot use.

    THE PAD RULE is here: a blank in a model's symbol table means index 0 for
    the MMS families and index 3 for Piper, and getting it wrong produces audio
    that is silent, clipped, or a fraction of a second long - never an error.
    """

    #: family -> (sample_rate_hz, pad_index, declares_rate_in_model)
    FAMILIES: dict[str, tuple[int, int, bool]] = {
        "mms": (16000, 0, True),
        "piper": (22050, 3, True),
        # Open JTalk voices do NOT declare their rate. Assuming the family
        # default plays Japanese at the wrong speed, which sounds like a broken
        # voice rather than a configuration error.
        "jsut-openjtalk": (22050, 0, False),
        "pocket": (24000, 0, True),
    }

    @classmethod
    def sample_rate_for(cls, family: str) -> int:
        return cls.FAMILIES.get(family.lower(), (22050, 0, True))[0]

    @classmethod
    def pad_index_for(cls, family: str) -> int:
        """0 for MMS, 3 for Piper. A wrong pad is never an error, only bad
        audio - which is why it is a table and not a guess."""
        return cls.FAMILIES.get(family.lower(), (22050, 0, True))[1]

    @classmethod
    def declares_rate(cls, family: str) -> bool:
        return cls.FAMILIES.get(family.lower(), (22050, 0, True))[2]

    @classmethod
    def known_families(cls) -> tuple[str, ...]:
        return tuple(sorted(cls.FAMILIES))


# ─────────────────────────────────────────────────────────────────────────────
# Verification


class VerificationLevel(IntEnum):
    """How much a claim about this code has actually been earned.

    ORDERED, and the order is the entire point. Everything below RAN_ON_DEVICE
    is a claim about a compiler, not about the thing working.
    """

    #: Written. Nobody has run it.
    UNVERIFIED = 0
    #: It compiles. Says nothing about behaviour, and is the level most often
    #: mistaken for the next one.
    COMPILES = 1
    #: Unit tests pass on a development machine.
    TESTED_LOCALLY = 2
    #: It ran on the target hardware and did the thing. THE ONLY LEVEL THAT
    #: COUNTS as done, because a desktop is a compile gate and a phone is the
    #: benchmark.
    RAN_ON_DEVICE = 3
    #: Ran on device and the numbers were recorded.
    MEASURED_ON_DEVICE = 4


@dataclass(frozen=True)
class CircleAIVerificationStatusAttribute:
    """Records what has actually been verified about a piece of code.

    An attribute rather than a comment so it can be COLLECTED - a build can list
    everything claiming RAN_ON_DEVICE and check that against what actually ran.
    A comment saying the same thing cannot be counted, and so drifts.
    """

    level: VerificationLevel = VerificationLevel.UNVERIFIED
    #: What it ran on. Required above TESTED_LOCALLY: "ran on device" without
    #: naming the device is the claim this type exists to stop.
    device: str = ""
    verified_on: str = ""
    note: str = ""

    def __post_init__(self) -> None:
        if self.level >= VerificationLevel.RAN_ON_DEVICE and not self.device:
            raise ValueError(
                "a claim that this ran on a device must name the device")

    @property
    def is_done(self) -> bool:
        return self.level >= VerificationLevel.RAN_ON_DEVICE

    def describe(self) -> str:
        if self.level is VerificationLevel.UNVERIFIED:
            return "not verified"
        if self.level is VerificationLevel.COMPILES:
            return "compiles - which says nothing about whether it works"
        if self.level is VerificationLevel.TESTED_LOCALLY:
            return "unit tested on a development machine, not on the target"
        where = f" on {self.device}" if self.device else ""
        when = f" ({self.verified_on})" if self.verified_on else ""
        if self.level is VerificationLevel.MEASURED_ON_DEVICE:
            return f"ran and was measured{where}{when}"
        return f"ran{where}{when}"


class Outcomes:
    """The names a diagnostic counter is allowed to take.

    A FIXED SET, because free-form outcome strings produce three spellings of
    the same thing and a dashboard that undercounts all three.
    """

    SUCCESS = "success"
    #: Refused on purpose. NOT a failure, and counting it as one makes a working
    #: safety gate look like an outage.
    REFUSED = "refused"
    FAILED = "failed"
    TIMED_OUT = "timed-out"
    CANCELLED = "cancelled"
    #: The device could not, and said so. Also not a failure.
    UNAVAILABLE = "unavailable"

    ALL = (SUCCESS, REFUSED, FAILED, TIMED_OUT, CANCELLED, UNAVAILABLE)

    @classmethod
    def is_bad(cls, outcome: str) -> bool:
        """Only two of the six mean something went wrong."""
        return outcome in (cls.FAILED, cls.TIMED_OUT)


class CircleAIDiagnostics:
    """Counters and timings, in memory, on the device.

    NOTHING LEAVES. There is no exporter, no endpoint and no identifier here,
    and that is deliberate: telemetry that reaches a server is a record of what
    somebody asked their phone, however aggregated it claims to be.
    """

    def __init__(self, now: Callable[[], datetime] | None = None) -> None:
        self._now = now or (lambda: datetime.now(timezone.utc))
        self._lock = threading.Lock()
        self._counters: dict[tuple[str, str], int] = {}
        self._durations: dict[str, list[float]] = {}
        self._started = self._now()

    def count(self, operation: str, outcome: str = Outcomes.SUCCESS) -> None:
        if outcome not in Outcomes.ALL:
            raise ValueError(
                f"{outcome!r} is not a known outcome; use one of {Outcomes.ALL}")
        with self._lock:
            key = (operation, outcome)
            self._counters[key] = self._counters.get(key, 0) + 1

    def observe(self, operation: str, milliseconds: float) -> None:
        with self._lock:
            self._durations.setdefault(operation, []).append(milliseconds)

    def counter(self, operation: str, outcome: str = Outcomes.SUCCESS) -> int:
        with self._lock:
            return self._counters.get((operation, outcome), 0)

    def percentile(self, operation: str, fraction: float = 0.95) -> float:
        """A real percentile from the samples held, or 0 when there are none.

        NEAREST-RANK, not interpolated: with the handful of samples a phone
        accumulates, an interpolated p95 reports a duration that never happened.
        """
        with self._lock:
            samples = sorted(self._durations.get(operation, ()))
        if not samples:
            return 0.0
        index = max(0, min(len(samples) - 1,
                           int(round(fraction * len(samples) + 0.5)) - 1))
        return samples[index]

    def snapshot(self) -> dict[str, object]:
        with self._lock:
            counters = {f"{op}.{outcome}": n for (op, outcome), n in self._counters.items()}
            operations = sorted(self._durations)
        return {
            "uptime_seconds": (self._now() - self._started).total_seconds(),
            "counters": counters,
            "p50": {op: self.percentile(op, 0.50) for op in operations},
            "p95": {op: self.percentile(op, 0.95) for op in operations},
        }

    def reset(self) -> None:
        with self._lock:
            self._counters.clear()
            self._durations.clear()


class CircleAIComponentBase:
    """What a UI component gets for free.

    Not a framework base class - there is no framework here. It carries the
    device context and the diagnostics handle so a component never reaches for a
    global to find either, which is what makes the same component testable and
    the same code usable from a head that is not a UI at all.
    """

    def __init__(
        self,
        context: SystemInfoDeviceContext | None = None,
        diagnostics: CircleAIDiagnostics | None = None,
    ) -> None:
        self._context = context or SystemInfoDeviceContext()
        self._diagnostics = diagnostics or CircleAIDiagnostics()
        self._disposed = False

    @property
    def device(self) -> SystemInfoDeviceContext:
        return self._context

    @property
    def diagnostics(self) -> CircleAIDiagnostics:
        return self._diagnostics

    @property
    def is_disposed(self) -> bool:
        return self._disposed

    def dispose(self) -> None:
        """IDEMPOTENT. A component is disposed by a navigation and by a parent
        teardown, and often by both within a frame of each other."""
        self._disposed = True
