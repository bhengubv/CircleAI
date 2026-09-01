"""The web surface, translation, the SQL seam, and the last of the runtime.

WHAT MAKES A WEB SURFACE DIFFERENT HERE: it is served BY the device, to the
person holding it or to somebody on the same link. There is no origin server, so
the cache is not a performance trick - it is the thing that makes a page work
when the radio is off, which is the normal state rather than the exception.

THE SQL SEAM PARAMETERISES EVERYTHING. Every value stored here came from
something somebody said to an assistant, and a store that concatenates strings
into SQL can be rewritten by saying the right sentence out loud.
"""

from __future__ import annotations

import math
import re
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Sequence


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# Web


@dataclass(frozen=True)
class PageMetadata:
    """What a page says about itself."""

    title: str = ""
    description: str = ""
    #: The path this page IS. Used for the canonical link and for cache keys, so
    #: it must be the normalised form rather than whatever was typed.
    path: str = "/"
    #: When it stops being worth trusting. None means it does not expire, which
    #: is right for a static page and wrong for anything with data on it.
    expires_at: datetime | None = None
    language: str = ""

    def is_fresh_at(self, when: datetime) -> bool:
        return self.expires_at is None or when < self.expires_at


@dataclass(frozen=True)
class RouteDescriptor:
    """One route the device serves."""

    path: str
    handler: str = ""
    #: Whether this route may be reached from OFF the device. Off by default:
    #: the common case is a page for the person holding the phone, and a route
    #: that is reachable by everything on the café Wi-Fi should be a decision.
    is_public: bool = False
    #: Whether a cached copy may answer. A page showing a balance should say no
    #: however convenient a stale answer would be.
    cacheable: bool = True
    methods: tuple[str, ...] = ("GET",)

    @staticmethod
    def normalise(path: str) -> str:
        """Collapses slashes, strips a trailing one, and lower-cases.

        Without this `/Chat`, `/chat/` and `//chat` are three cache entries and
        three routes, and the third one usually 404s.
        """
        collapsed = re.sub(r"/{2,}", "/", (path or "/").strip())
        trimmed = collapsed.rstrip("/") or "/"
        return trimmed.lower()

    def matches(self, path: str, method: str = "GET") -> bool:
        return (
            self.normalise(path) == self.normalise(self.path)
            and method.upper() in self.methods
        )


@dataclass(frozen=True)
class CachedResponse:
    """A page kept for when the radio is off."""

    body: str = ""
    media_type: str = "text/html"
    stored_at: datetime = field(default_factory=_now)
    #: How long it stays fresh. After that it is STALE, which is not the same as
    #: gone: a stale page shown with a note beats a blank screen when there is
    #: no way to fetch a new one.
    ttl: timedelta = timedelta(minutes=15)
    etag: str = ""

    def is_fresh_at(self, when: datetime) -> bool:
        return when - self.stored_at < self.ttl

    def age_at(self, when: datetime) -> timedelta:
        return max(timedelta(), when - self.stored_at)

    def staleness_note(self, when: datetime) -> str:
        """What to tell somebody looking at an old page.

        In minutes and hours rather than a timestamp, because "as of 14:03" does
        not tell a person whether that is now.
        """
        if self.is_fresh_at(when):
            return ""
        age = self.age_at(when)
        minutes = int(age.total_seconds() // 60)
        if minutes < 60:
            return f"this is {minutes} minutes old - there was no connection to refresh it"
        return f"this is {minutes // 60} hours old - there was no connection to refresh it"


class IWebBoard(ABC):
    """The pages this device serves."""

    @abstractmethod
    def routes(self) -> Sequence[RouteDescriptor]: ...

    @abstractmethod
    def get(self, path: str, method: str = "GET") -> tuple[CachedResponse | None, str]: ...

    @abstractmethod
    def put(self, path: str, response: CachedResponse) -> None: ...


class InMemoryWebBoard(IWebBoard):
    """Routes and their cached bodies, in memory."""

    def __init__(
        self,
        routes: Sequence[RouteDescriptor] = (),
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._now = now or _now
        self._lock = threading.Lock()
        self._routes = {RouteDescriptor.normalise(r.path): r for r in routes}
        self._cache: dict[str, CachedResponse] = {}

    def routes(self) -> Sequence[RouteDescriptor]:
        with self._lock:
            return tuple(self._routes.values())

    def add_route(self, route: RouteDescriptor) -> None:
        with self._lock:
            self._routes[RouteDescriptor.normalise(route.path)] = route

    def get(self, path: str, method: str = "GET") -> tuple[CachedResponse | None, str]:
        """Returns (response, note). The note is the staleness warning, empty
        when the page is fresh - so a caller cannot serve a stale page without
        having been handed the words to say so."""
        key = RouteDescriptor.normalise(path)
        with self._lock:
            route = self._routes.get(key)
            cached = self._cache.get(key)
        if route is None:
            return None, "there is no page at that address"
        if method.upper() not in route.methods:
            return None, f"{method.upper()} is not something that page accepts"
        if cached is None:
            return None, ""
        if not route.cacheable:
            # A route marked uncacheable does not serve a stale copy EVEN when
            # one is sitting there. A balance shown from an hour ago is worse
            # than no balance.
            return (cached, "") if cached.is_fresh_at(self._now()) else (None, "")
        return cached, cached.staleness_note(self._now())

    def put(self, path: str, response: CachedResponse) -> None:
        with self._lock:
            self._cache[RouteDescriptor.normalise(path)] = response

    def invalidate(self, path: str = "") -> int:
        with self._lock:
            if not path:
                count = len(self._cache)
                self._cache.clear()
                return count
            return 1 if self._cache.pop(RouteDescriptor.normalise(path), None) else 0


class WebCompanionService:
    """The companion behind a web page served by the device.

    EVERY RESPONSE IS RENDERED WITH ESCAPING. The text comes from a model and
    the model's input came from a person, so a page that interpolates it raw is
    a page anybody can put script into by asking the assistant to repeat
    something.
    """

    def __init__(
        self,
        board: IWebBoard | None = None,
        respond: Callable[[str], str] | None = None,
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._board = board or InMemoryWebBoard()
        self._respond = respond
        self._now = now or _now

    @staticmethod
    def escape(text: str) -> str:
        """Ampersand FIRST.

        Escaping the angle brackets first would then escape the ampersands it
        just introduced, turning `&lt;` into `&amp;lt;` and showing the markup
        to the reader.
        """
        return (
            text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            .replace('"', "&quot;").replace("'", "&#39;")
        )

    @property
    def board(self) -> IWebBoard:
        return self._board

    def render(self, metadata: PageMetadata, body_text: str) -> str:
        lang = f' lang="{self.escape(metadata.language)}"' if metadata.language else ""
        return (
            f'<article{lang}><h1>{self.escape(metadata.title)}</h1>'
            f'<p>{self.escape(body_text)}</p></article>'
        )

    def ask(self, path: str, question: str) -> tuple[str, str]:
        """Returns (html, note). Serves a cached answer when there is one and
        the device cannot produce a new one."""
        cached, note = self._board.get(path)
        if self._respond is None:
            if cached is not None:
                return cached.body, note or "this device cannot answer right now"
            return "", "this device cannot answer right now"
        html = self.render(PageMetadata(title=question, path=path), self._respond(question))
        self._board.put(path, CachedResponse(html, "text/html", self._now()))
        return html, ""


class McpEndpoints:
    """The tool-protocol surface.

    TOOLS ARE LISTED ONLY IF THEY ARE ALLOWED. Advertising a tool that will then
    refuse teaches a caller to try it every time, and each attempt is a prompt
    that reached a model and a refusal that reached a person.
    """

    def __init__(
        self,
        tools: Sequence[dict[str, object]] = (),
        invoke: Callable[[str, dict[str, object]], object] | None = None,
        is_allowed: Callable[[str], bool] | None = None,
    ) -> None:
        self._tools = tuple(tools)
        self._invoke = invoke
        self._is_allowed = is_allowed

    def list_tools(self) -> list[dict[str, object]]:
        return [
            t for t in self._tools
            if self._is_allowed is None or self._is_allowed(str(t.get("name", "")))
        ]

    def call_tool(self, name: str, arguments: dict[str, object]) -> dict[str, object]:
        if self._is_allowed is not None and not self._is_allowed(name):
            return {"isError": True, "content": [
                {"type": "text", "text": f"{name} is not available on this device"}]}
        if self._invoke is None:
            return {"isError": True, "content": [
                {"type": "text", "text": "no tools are wired up on this device"}]}
        try:
            result = self._invoke(name, arguments)
        except Exception as exc:  # noqa: BLE001
            # The error goes back as CONTENT with isError set, not as a
            # transport failure. A tool that raised is a result the model should
            # see and work around, not a broken connection it should retry.
            return {"isError": True, "content": [{"type": "text", "text": str(exc)}]}
        return {"isError": False, "content": [{"type": "text", "text": str(result)}]}


class ToolCatalogExtensions:
    """Filtering and describing a tool catalogue."""

    @staticmethod
    def visible_to(
        tools: Sequence[dict[str, object]], granted: Sequence[str]
    ) -> list[dict[str, object]]:
        allowed = {g.strip().lower() for g in granted if g.strip()}
        return [t for t in tools if str(t.get("name", "")).lower() in allowed]

    @staticmethod
    def describe(tools: Sequence[dict[str, object]]) -> str:
        """Names only, never the schemas.

        This is shown to a PERSON asking what the assistant can do. A JSON
        schema answers a different question, for a different reader.
        """
        if not tools:
            return "this device has no tools wired up"
        return "this can: " + ", ".join(
            str(t.get("description") or t.get("name", "")) for t in tools)


class GeneratorIds:
    """The image generators a host may consent to."""

    LOCAL = "local"
    OPENAI_IMAGE = "openai-image"
    STABILITY = "stability"
    REPLICATE = "replicate"

    ALL = (LOCAL, OPENAI_IMAGE, STABILITY, REPLICATE)

    @staticmethod
    def is_local(generator_id: str) -> bool:
        """Which ones keep the prompt on the device.

        Worth its own function because it is the question that decides whether a
        person needs to be asked - and every other generator in the list sends
        the prompt, and often the reference image, to somebody else.
        """
        return generator_id.strip().lower() == GeneratorIds.LOCAL


class RealtimePackageMarker:
    """Names this package, so a host can tell whether it is present.

    A marker rather than a flag somebody sets: a build either has this module or
    it does not, and asking the module is the only answer that cannot drift from
    the truth.
    """

    NAME = "circle_ai.realtime"

    @staticmethod
    def is_present() -> bool:
        return True


@dataclass(frozen=True)
class VoiceOptions:
    """How the device listens and speaks."""

    #: OFF. A microphone that opens because a build carries the capability is
    #: the thing this default exists to prevent.
    wake_word_enabled: bool = False
    wake_phrases: tuple[str, ...] = ()
    voice_id: str = ""
    language: str = ""
    speaking_rate: float = 1.0
    #: Whether audio may be kept after it has been transcribed. NO. Kept as an
    #: option because a host may need it for a debugging build, and as False so
    #: that build has to say so.
    retain_audio: bool = False
    barge_in_enabled: bool = True

    def __post_init__(self) -> None:
        if self.wake_word_enabled and not self.wake_phrases:
            raise ValueError(
                "a wake word cannot be enabled without a phrase to wake on")


@dataclass(frozen=True)
class SystemPromptEnrichment:
    """What gets added to a system prompt, and what it costs.

    BUDGETED IN CHARACTERS. Every enrichment competes with the conversation for
    the model's context, and an unbudgeted one grows until the earliest turns
    fall out of the window - which reads as the assistant forgetting what was
    just said.
    """

    device_context: str = ""
    recalled_memory: str = ""
    active_skills: str = ""
    time_and_place: str = ""
    #: The ceiling. Anything past it is dropped in REVERSE PRIORITY order, so
    #: the device context survives and the recalled memory is what goes.
    max_characters: int = 2000

    def sections(self) -> list[tuple[str, str]]:
        """Most important first, which is the order things are KEPT in."""
        return [
            (name, value) for name, value in (
                ("device", self.device_context),
                ("now", self.time_and_place),
                ("skills", self.active_skills),
                ("memory", self.recalled_memory),
            ) if value.strip()
        ]

    def build(self) -> str:
        """Drops whole sections rather than truncating one.

        Half a recalled memory is worse than none: the model reads the fragment
        as a complete fact and answers from it.
        """
        out: list[str] = []
        used = 0
        for name, value in self.sections():
            block = f"[{name}]\n{value}"
            if used + len(block) > self.max_characters:
                continue
            out.append(block)
            used += len(block) + 1
        return "\n".join(out)

    @property
    def was_truncated(self) -> bool:
        return len(self.build()) < sum(
            len(f"[{n}]\n{v}") + 1 for n, v in self.sections())


class NeuronServiceCollectionExtensions:
    """Wires the on-device brain."""

    def __init__(self) -> None:
        self._registered: dict[str, object] = {}

    def add_neuron(self, name: str, service: object) -> "NeuronServiceCollectionExtensions":
        self._registered[name] = service
        return self

    def build(self) -> dict[str, object]:
        return dict(self._registered)

    def names(self) -> tuple[str, ...]:
        return tuple(sorted(self._registered))


class IPayloadOptimiser(ABC):
    """Makes a message smaller before it goes over a radio.

    WORTH ITS OWN SEAM because the radios here are measured: Wi-Fi Direct
    carries about fifty messages a second and BLE about nine, one way. At nine a
    second, the difference between a 200-byte message and a 900-byte one is the
    difference between a protocol that works and one that does not.
    """

    @abstractmethod
    def optimise(self, payload: bytes, max_bytes: int) -> tuple[bytes, bool]:
        """Returns (payload, was_changed). A payload that already fits comes
        back untouched, so a caller can tell "small enough" from "made
        smaller"."""

    @abstractmethod
    def restore(self, payload: bytes) -> bytes: ...


# ─────────────────────────────────────────────────────────────────────────────
# Translation


class ITranslationEngine(ABC):
    """Translates text."""

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def translate(self, text: str, to_language: str, from_language: str = "") -> str: ...


class ILiveTranslator(ABC):
    """Translates a conversation as it happens."""

    @abstractmethod
    def push(self, text: str, is_final: bool) -> str:
        """Partial text in, partial translation out.

        The hard constraint: a partial translation must be allowed to CHANGE
        when more words arrive. Word order differs between languages, so the
        first half of a sentence in one is often the second half in another, and
        a translator that only appends produces nonsense.
        """

    @abstractmethod
    def reset(self) -> None: ...


class LlmTranslationEngine(ITranslationEngine):
    """Translation through the on-device model.

    THE PROMPT SAYS "TRANSLATE, DO NOT ANSWER". A model given a question in
    another language will answer it, helpfully and wrongly, and the person is
    then reading a reply to a question they were trying to pass on.
    """

    def __init__(
        self,
        generate: Callable[[str], str] | None = None,
        max_characters: int = 4000,
    ) -> None:
        self._generate = generate
        self._max = max_characters

    @property
    def is_available(self) -> bool:
        return self._generate is not None

    def build_prompt(self, text: str, to_language: str, from_language: str = "") -> str:
        source = f" from {from_language}" if from_language else ""
        return (
            f"Translate the following{source} into {to_language}. "
            f"Output only the translation. Do not answer it, do not explain it, "
            f"and do not add anything.\n\n{text}"
        )

    def translate(self, text: str, to_language: str, from_language: str = "") -> str:
        if not self.is_available:
            raise RuntimeError("no translation engine is available on this device")
        if not text.strip():
            return ""
        if to_language.strip().lower() == from_language.strip().lower() and from_language:
            # Same language in and out: return it UNCHANGED rather than round-
            # tripping through a model that will rephrase it.
            return text
        if len(text) > self._max:
            # Split on sentence ends rather than at a character count. Cutting
            # mid-sentence gives the model half a clause and it invents the
            # rest.
            parts = re.split(r"(?<=[.!?])\s+", text)
            chunks: list[str] = []
            current = ""
            for part in parts:
                if len(current) + len(part) > self._max and current:
                    chunks.append(current)
                    current = part
                else:
                    current = f"{current} {part}".strip()
            if current:
                chunks.append(current)
            return " ".join(
                self._generate(self.build_prompt(c, to_language, from_language)).strip()
                for c in chunks)
        return self._generate(
            self.build_prompt(text, to_language, from_language)).strip()


class LiveTranslator(ILiveTranslator):
    """Live translation that is allowed to revise itself."""

    def __init__(
        self,
        engine: ITranslationEngine | None = None,
        to_language: str = "",
        from_language: str = "",
    ) -> None:
        self._engine = engine
        self._to = to_language
        self._from = from_language
        self._buffer = ""
        self._settled: list[str] = []

    def push(self, text: str, is_final: bool) -> str:
        if self._engine is None or not self._engine.is_available:
            return ""
        self._buffer = text
        if not text.strip():
            return " ".join(self._settled)
        translated = self._engine.translate(text, self._to, self._from)
        if is_final:
            # Only a FINAL segment is settled. A partial one is re-translated
            # from scratch every time, which is what lets the word order change
            # as the sentence completes.
            self._settled.append(translated)
            self._buffer = ""
            return " ".join(self._settled)
        return " ".join([*self._settled, translated])

    def reset(self) -> None:
        self._buffer = ""
        self._settled.clear()
