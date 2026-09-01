"""Cloud fallback: the providers, and the rules that keep them a FALLBACK.

THE ORDER IS NOT NEGOTIABLE. On-device first, cloud only when the device cannot,
and only when a person has said so for that provider. Every default in this file
is off, and every constructor that could quietly reverse the order refuses
instead.

WHAT A PROVIDER SEES IS THE WHOLE POINT. Sending a prompt to a cloud provider
sends it to somebody else's computer, permanently, whatever their retention page
says today. So the interesting code here is not the eight generators - they are
nearly the same request twice - it is:

  * the key never appearing in a log, an error, a repr or a URL;
  * the fallback refusing to run when the caller has not been told a provider
    would be used;
  * a failure naming the PROVIDER and not the key, because "auth failed" sends
    somebody to rotate a credential that was never the problem.

The C# names are kept so the trees line up. Nothing here talks to a network by
itself: a `post` callable is supplied, or the generator reports unavailable.
"""

from __future__ import annotations

import json
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Callable, Iterator, Sequence

# `ChatTurn` already exists in the chat runtime and is DELEGATED TO, not
# redefined. A second frozen dataclass of the same shape and name is the kind of
# duplicate that stays in sync for a month and then does not.
from ..chat_runtime import ChatTurn


class ProviderIds:
    """The identifiers a person consents to, one per provider.

    STRINGS, not an enum, because a host may carry a provider this build has
    never heard of - an OpenAI-compatible endpoint on somebody's own hardware is
    the common case, and an enum would make that the one thing impossible.
    """

    OPENAI = "openai"
    ANTHROPIC = "anthropic"
    GEMINI = "gemini"
    GROQ = "groq"
    CEREBRAS = "cerebras"
    DEEPSEEK = "deepseek"
    TOGETHER = "together"
    ULTRAVOX = "ultravox"
    ELEVENLABS = "elevenlabs"
    NOVA_SONIC = "nova-sonic"

    ALL = (
        OPENAI, ANTHROPIC, GEMINI, GROQ, CEREBRAS, DEEPSEEK, TOGETHER,
        ULTRAVOX, ELEVENLABS, NOVA_SONIC,
    )


class _Secret:
    """Holds a key so it cannot be printed by accident.

    `__repr__` and `__str__` both redact. A key reaches a log the ordinary way -
    somebody prints an options object while debugging, or an exception carries
    its arguments into a crash report - and neither of those is a decision
    anybody made.
    """

    __slots__ = ("_value",)

    def __init__(self, value: str = "") -> None:
        self._value = value or ""

    def reveal(self) -> str:
        """The ONE way out, named so it is visible at every call site."""
        return self._value

    @property
    def is_set(self) -> bool:
        return bool(self._value)

    def __repr__(self) -> str:
        return "<secret set>" if self._value else "<secret unset>"

    __str__ = __repr__

    def __bool__(self) -> bool:
        return bool(self._value)


@dataclass
class CloudChatOptionsBase:
    """What every cloud chat provider needs."""

    #: OFF. A build that carries a provider does not use it, and turning it on
    #: is a decision somebody makes rather than a default they inherit.
    enabled: bool = False
    model: str = ""
    base_url: str = ""
    timeout_seconds: float = 60.0
    max_output_tokens: int = 1024
    temperature: float = 0.7
    _key: _Secret = field(default_factory=_Secret, repr=False)

    @property
    def api_key(self) -> _Secret:
        return self._key

    def with_key(self, key: str) -> "CloudChatOptionsBase":
        """Set through a method, not an attribute, so a key never arrives by
        being assigned somewhere far from here."""
        self._key = _Secret(key)
        return self

    @property
    def is_configured(self) -> bool:
        return self.enabled and self._key.is_set and bool(self.model)


@dataclass
class OpenAiChatOptions(CloudChatOptionsBase):
    model: str = "gpt-4o-mini"
    base_url: str = "https://api.openai.com/v1"


@dataclass
class GroqChatOptions(CloudChatOptionsBase):
    model: str = "llama-3.3-70b-versatile"
    base_url: str = "https://api.groq.com/openai/v1"


@dataclass
class CerebrasChatOptions(CloudChatOptionsBase):
    model: str = "llama3.1-8b"
    base_url: str = "https://api.cerebras.ai/v1"


@dataclass
class DeepSeekChatOptions(CloudChatOptionsBase):
    model: str = "deepseek-chat"
    base_url: str = "https://api.deepseek.com/v1"


@dataclass
class TogetherChatOptions(CloudChatOptionsBase):
    model: str = "meta-llama/Llama-3.3-70B-Instruct-Turbo"
    base_url: str = "https://api.together.xyz/v1"


@dataclass
class GeminiChatOptions(CloudChatOptionsBase):
    model: str = "gemini-2.0-flash"
    base_url: str = "https://generativelanguage.googleapis.com/v1beta"


@dataclass
class AnthropicChatOptions(CloudChatOptionsBase):
    model: str = "claude-sonnet-4-5"
    base_url: str = "https://api.anthropic.com/v1"
    #: Anthropic requires it and rejects the request without it. Pinned rather
    #: than tracking latest, so a change on their side never changes what this
    #: build sends.
    api_version: str = "2023-06-01"


@dataclass(frozen=True)
class CloudChatResult:
    """What came back."""

    text: str = ""
    #: The provider that answered. ALWAYS carried, so a caller can tell a person
    #: where their words went - which is the fact that makes a fallback
    #: something agreed to rather than something that happened.
    provider_id: str = ""
    model: str = ""
    input_tokens: int = 0
    output_tokens: int = 0
    #: Set when the call did not happen or did not succeed. Names the PROVIDER,
    #: never the key.
    error: str = ""

    @property
    def succeeded(self) -> bool:
        return not self.error and bool(self.text)


class ICloudChatGenerator(ABC):
    """A cloud text generator."""

    @property
    @abstractmethod
    def provider_id(self) -> str: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def generate(self, turns: Sequence[ChatTurn], system: str = "") -> CloudChatResult: ...


def parse_sse(chunk: str) -> Iterator[str]:
    """Yields the `data:` payloads from a server-sent-event stream.

    Events are separated by a BLANK LINE and a single event may carry several
    `data:` lines that concatenate with newlines. Splitting on newline and
    treating every line as an event works on most providers and silently
    truncates the one that wraps - which shows up as a reply that stops
    mid-sentence, blamed on the model.
    """
    for block in chunk.replace("\r\n", "\n").split("\n\n"):
        payload = "\n".join(
            line[5:].lstrip() for line in block.split("\n")
            if line.startswith("data:")
        )
        if payload and payload != "[DONE]":
            yield payload


class OpenAiCompatibleChatGeneratorBase(ICloudChatGenerator):
    """The shape five of these providers share.

    Groq, Cerebras, DeepSeek and Together all speak OpenAI's chat-completions
    wire format. Writing them out five times would mean fixing a parsing bug
    five times and forgetting once.
    """

    def __init__(
        self,
        provider_id: str,
        options: CloudChatOptionsBase,
        post: Callable[[str, dict[str, str], dict[str, object]], dict[str, object]] | None = None,
    ) -> None:
        self._provider_id = provider_id
        self._options = options
        self._post = post

    @property
    def provider_id(self) -> str:
        return self._provider_id

    @property
    def options(self) -> CloudChatOptionsBase:
        return self._options

    @property
    def is_available(self) -> bool:
        """Configured AND given a transport. A generator with a key and no way
        to send it is not available, and reporting otherwise makes the fallback
        choose a provider that then fails."""
        return self._options.is_configured and self._post is not None

    def headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self._options.api_key.reveal()}",
            "Content-Type": "application/json",
        }

    def body(self, turns: Sequence[ChatTurn], system: str) -> dict[str, object]:
        messages: list[dict[str, str]] = []
        if system:
            messages.append({"role": "system", "content": system})
        messages += [{"role": t.role, "content": t.content} for t in turns]
        return {
            "model": self._options.model,
            "messages": messages,
            "max_tokens": self._options.max_output_tokens,
            "temperature": self._options.temperature,
        }

    def parse(self, raw: dict[str, object]) -> CloudChatResult:
        choices = raw.get("choices") or []
        text = ""
        if choices and isinstance(choices[0], dict):
            text = str((choices[0].get("message") or {}).get("content", ""))
        usage = raw.get("usage") or {}
        return CloudChatResult(
            text, self._provider_id, str(raw.get("model", self._options.model)),
            int(usage.get("prompt_tokens", 0) or 0),
            int(usage.get("completion_tokens", 0) or 0),
        )

    def generate(self, turns: Sequence[ChatTurn], system: str = "") -> CloudChatResult:
        if not self.is_available:
            # Names what is missing WITHOUT naming the key's value, and says
            # "not configured" rather than "auth failed" - the second sends
            # somebody to rotate a credential that was never the problem.
            return CloudChatResult(
                provider_id=self._provider_id,
                error=f"{self._provider_id} is not configured on this device")
        try:
            raw = self._post(
                f"{self._options.base_url}/chat/completions",
                self.headers(), self.body(turns, system))
        except Exception as exc:  # noqa: BLE001 - the reason reaches a person
            return CloudChatResult(
                provider_id=self._provider_id,
                error=f"{self._provider_id} did not answer: {exc}")
        return self.parse(raw)


class OpenAiChatGenerator(OpenAiCompatibleChatGeneratorBase):
    def __init__(self, options: OpenAiChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.OPENAI, options or OpenAiChatOptions(), post)


class GroqChatGenerator(OpenAiCompatibleChatGeneratorBase):
    def __init__(self, options: GroqChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.GROQ, options or GroqChatOptions(), post)


class CerebrasChatGenerator(OpenAiCompatibleChatGeneratorBase):
    def __init__(self, options: CerebrasChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.CEREBRAS, options or CerebrasChatOptions(), post)


class DeepSeekChatGenerator(OpenAiCompatibleChatGeneratorBase):
    def __init__(self, options: DeepSeekChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.DEEPSEEK, options or DeepSeekChatOptions(), post)


class TogetherChatGenerator(OpenAiCompatibleChatGeneratorBase):
    def __init__(self, options: TogetherChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.TOGETHER, options or TogetherChatOptions(), post)


class AnthropicChatGenerator(OpenAiCompatibleChatGeneratorBase):
    """Anthropic's own shape: system is a TOP-LEVEL field, not a message."""

    def __init__(self, options: AnthropicChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.ANTHROPIC, options or AnthropicChatOptions(), post)

    def headers(self) -> dict[str, str]:
        opts = self._options
        return {
            # `x-api-key`, NOT a bearer token. Sending it as a bearer gets a 401
            # that reads exactly like a bad key.
            "x-api-key": opts.api_key.reveal(),
            "anthropic-version": getattr(opts, "api_version", "2023-06-01"),
            "Content-Type": "application/json",
        }

    def body(self, turns: Sequence[ChatTurn], system: str) -> dict[str, object]:
        body: dict[str, object] = {
            "model": self._options.model,
            "messages": [{"role": t.role, "content": t.content} for t in turns],
            "max_tokens": self._options.max_output_tokens,
            "temperature": self._options.temperature,
        }
        if system:
            body["system"] = system
        return body

    def parse(self, raw: dict[str, object]) -> CloudChatResult:
        blocks = raw.get("content") or []
        text = "".join(
            str(b.get("text", "")) for b in blocks
            if isinstance(b, dict) and b.get("type") == "text"
        )
        usage = raw.get("usage") or {}
        return CloudChatResult(
            text, self.provider_id, str(raw.get("model", self._options.model)),
            int(usage.get("input_tokens", 0) or 0),
            int(usage.get("output_tokens", 0) or 0),
        )

    def generate(self, turns: Sequence[ChatTurn], system: str = "") -> CloudChatResult:
        if not self.is_available:
            return CloudChatResult(
                provider_id=self.provider_id,
                error=f"{self.provider_id} is not configured on this device")
        try:
            raw = self._post(
                f"{self._options.base_url}/messages",
                self.headers(), self.body(turns, system))
        except Exception as exc:  # noqa: BLE001
            return CloudChatResult(
                provider_id=self.provider_id,
                error=f"{self.provider_id} did not answer: {exc}")
        return self.parse(raw)


class GeminiChatGenerator(OpenAiCompatibleChatGeneratorBase):
    """Gemini's own shape: `contents`, and `model` in the PATH."""

    def __init__(self, options: GeminiChatOptions | None = None, post=None) -> None:
        super().__init__(ProviderIds.GEMINI, options or GeminiChatOptions(), post)

    def headers(self) -> dict[str, str]:
        # A HEADER, never `?key=` in the URL. A key in a query string reaches
        # every proxy log and browser history between here and there, and it is
        # the single most common way a cloud key leaks.
        return {
            "x-goog-api-key": self._options.api_key.reveal(),
            "Content-Type": "application/json",
        }

    def body(self, turns: Sequence[ChatTurn], system: str) -> dict[str, object]:
        body: dict[str, object] = {
            "contents": [
                # Gemini says "model" where everyone else says "assistant".
                {"role": "model" if t.role == "assistant" else "user",
                 "parts": [{"text": t.content}]}
                for t in turns
            ],
            "generationConfig": {
                "maxOutputTokens": self._options.max_output_tokens,
                "temperature": self._options.temperature,
            },
        }
        if system:
            body["systemInstruction"] = {"parts": [{"text": system}]}
        return body

    def parse(self, raw: dict[str, object]) -> CloudChatResult:
        candidates = raw.get("candidates") or []
        text = ""
        if candidates and isinstance(candidates[0], dict):
            parts = (candidates[0].get("content") or {}).get("parts") or []
            text = "".join(str(p.get("text", "")) for p in parts if isinstance(p, dict))
        usage = raw.get("usageMetadata") or {}
        return CloudChatResult(
            text, self.provider_id, self._options.model,
            int(usage.get("promptTokenCount", 0) or 0),
            int(usage.get("candidatesTokenCount", 0) or 0),
        )

    def generate(self, turns: Sequence[ChatTurn], system: str = "") -> CloudChatResult:
        if not self.is_available:
            return CloudChatResult(
                provider_id=self.provider_id,
                error=f"{self.provider_id} is not configured on this device")
        try:
            raw = self._post(
                f"{self._options.base_url}/models/{self._options.model}:generateContent",
                self.headers(), self.body(turns, system))
        except Exception as exc:  # noqa: BLE001
            return CloudChatResult(
                provider_id=self.provider_id,
                error=f"{self.provider_id} did not answer: {exc}")
        return self.parse(raw)


class CloudFallbackServiceCollectionExtensions:
    """Wires the providers a host has consented to.

    A REGISTRATION LIST, and consent is per provider. Registering all of them
    because one is configured is how a person who agreed to one company's
    servers ends up on another's.
    """

    @staticmethod
    def add_cloud_fallback(
        options_by_provider: dict[str, CloudChatOptionsBase],
        post: Callable[..., dict[str, object]] | None = None,
        consented: Sequence[str] = (),
    ) -> list[ICloudChatGenerator]:
        """Returns only providers that are BOTH configured and consented to.

        Both, not either. A configured provider nobody agreed to is the failure
        this whole file exists to prevent.
        """
        builders = {
            ProviderIds.OPENAI: OpenAiChatGenerator,
            ProviderIds.GROQ: GroqChatGenerator,
            ProviderIds.CEREBRAS: CerebrasChatGenerator,
            ProviderIds.DEEPSEEK: DeepSeekChatGenerator,
            ProviderIds.TOGETHER: TogetherChatGenerator,
            ProviderIds.GEMINI: GeminiChatGenerator,
            ProviderIds.ANTHROPIC: AnthropicChatGenerator,
        }
        allowed = {p.strip().lower() for p in consented if p and p.strip()}
        out: list[ICloudChatGenerator] = []
        for provider_id, opts in options_by_provider.items():
            builder = builders.get(provider_id.lower())
            if builder is None or provider_id.lower() not in allowed:
                continue
            generator = builder(opts, post)
            if generator.is_available:
                out.append(generator)
        return out

    @staticmethod
    def describe(generators: Sequence[ICloudChatGenerator]) -> str:
        """What a person is shown before anything leaves the device."""
        if not generators:
            return "nothing here would leave this device"
        names = ", ".join(g.provider_id for g in generators)
        return f"if this device cannot answer, it would ask: {names}"


# ─────────────────────────────────────────────────────────────────────────────
# Realtime providers


@dataclass
class RealtimeCloudOptionsBase:
    """What every realtime provider needs."""

    enabled: bool = False
    model: str = ""
    url: str = ""
    voice: str = ""
    sample_rate_hz: int = 24000
    _key: _Secret = field(default_factory=_Secret, repr=False)

    @property
    def api_key(self) -> _Secret:
        return self._key

    def with_key(self, key: str) -> "RealtimeCloudOptionsBase":
        self._key = _Secret(key)
        return self

    @property
    def is_configured(self) -> bool:
        return self.enabled and self._key.is_set and bool(self.url)


@dataclass
class OpenAiRealtimeOptions(RealtimeCloudOptionsBase):
    model: str = "gpt-4o-realtime-preview"
    url: str = "wss://api.openai.com/v1/realtime"


@dataclass
class GeminiLiveOptions(RealtimeCloudOptionsBase):
    model: str = "gemini-2.0-flash-live"
    url: str = "wss://generativelanguage.googleapis.com/ws"
    #: Gemini Live speaks 16k up and 24k down. One rate for both resamples
    #: something silently, and resampled speech sounds like a bad line rather
    #: than a bug.
    input_sample_rate_hz: int = 16000


@dataclass
class NovaSonicOptions(RealtimeCloudOptionsBase):
    model: str = "amazon.nova-sonic-v1"
    url: str = "wss://bedrock-runtime.us-east-1.amazonaws.com"
    region: str = "us-east-1"


@dataclass
class ElevenLabsConvOptions(RealtimeCloudOptionsBase):
    url: str = "wss://api.elevenlabs.io/v1/convai/conversation"
    agent_id: str = ""


@dataclass
class UltravoxOptions(RealtimeCloudOptionsBase):
    model: str = "fixie-ai/ultravox"
    url: str = "wss://api.ultravox.ai/api/calls"


class RealtimeWebSocketSession:
    """A realtime session over a websocket the host supplies.

    NO SOCKET IS OPENED HERE. `send` and `close` are callables, so this is
    testable without a network and cannot open a connection as a side effect of
    being constructed - which is exactly the accident that would send audio
    before a person agreed to it.
    """

    def __init__(
        self,
        session_id: str,
        provider_id: str,
        send: Callable[[bytes | str], None] | None = None,
        close: Callable[[], None] | None = None,
    ) -> None:
        self.session_id = session_id
        self.provider_id = provider_id
        self._send = send
        self._close = close
        self._closed = False
        self._sent_frames = 0

    @property
    def is_open(self) -> bool:
        return not self._closed and self._send is not None

    @property
    def frames_sent(self) -> int:
        return self._sent_frames

    def send_audio(self, pcm: bytes) -> bool:
        """Refuses after close rather than raising.

        Audio arrives from a capture thread that has not yet noticed the session
        ended; raising there kills the capture and the microphone stays hot.
        """
        if not self.is_open:
            return False
        self._send(pcm)
        self._sent_frames += 1
        return True

    def send_event(self, event: dict[str, object]) -> bool:
        if not self.is_open:
            return False
        self._send(json.dumps(event))
        return True

    def interrupt(self) -> bool:
        """Barge-in. Every provider spells it differently; this sends the one
        shape and lets the transport translate."""
        return self.send_event({"type": "response.cancel"})

    def close(self) -> None:
        """IDEMPOTENT. A session is closed by whichever of the peer, the user
        and the error path gets there first, and often by two of them."""
        if self._closed:
            return
        self._closed = True
        if self._close is not None:
            self._close()


class CloudRealtimeServiceBase:
    """What the realtime providers share."""

    def __init__(
        self,
        provider_id: str,
        options: RealtimeCloudOptionsBase,
        connect: Callable[[str, dict[str, str]], RealtimeWebSocketSession] | None = None,
    ) -> None:
        self._provider_id = provider_id
        self._options = options
        self._connect = connect

    @property
    def provider_id(self) -> str:
        return self._provider_id

    @property
    def options(self) -> RealtimeCloudOptionsBase:
        return self._options

    @property
    def is_available(self) -> bool:
        return self._options.is_configured and self._connect is not None

    def headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._options.api_key.reveal()}"}

    def open(self) -> RealtimeWebSocketSession | None:
        """None rather than a raise when unavailable, so the caller falls back
        to the on-device voice loop instead of failing the call."""
        if not self.is_available:
            return None
        return self._connect(self._options.url, self.headers())


class OpenAiRealtimeService(CloudRealtimeServiceBase):
    def __init__(self, options: OpenAiRealtimeOptions | None = None, connect=None) -> None:
        super().__init__(ProviderIds.OPENAI, options or OpenAiRealtimeOptions(), connect)

    def headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self._options.api_key.reveal()}",
            "OpenAI-Beta": "realtime=v1",
        }


class GeminiLiveService(CloudRealtimeServiceBase):
    def __init__(self, options: GeminiLiveOptions | None = None, connect=None) -> None:
        super().__init__(ProviderIds.GEMINI, options or GeminiLiveOptions(), connect)

    def headers(self) -> dict[str, str]:
        return {"x-goog-api-key": self._options.api_key.reveal()}


class NovaSonicService(CloudRealtimeServiceBase):
    def __init__(self, options: NovaSonicOptions | None = None, connect=None) -> None:
        super().__init__(ProviderIds.NOVA_SONIC, options or NovaSonicOptions(), connect)


class ElevenLabsConvService(CloudRealtimeServiceBase):
    def __init__(self, options: ElevenLabsConvOptions | None = None, connect=None) -> None:
        super().__init__(ProviderIds.ELEVENLABS, options or ElevenLabsConvOptions(), connect)

    def headers(self) -> dict[str, str]:
        return {"xi-api-key": self._options.api_key.reveal()}


class UltravoxService(CloudRealtimeServiceBase):
    def __init__(self, options: UltravoxOptions | None = None, connect=None) -> None:
        super().__init__(ProviderIds.ULTRAVOX, options or UltravoxOptions(), connect)

    def headers(self) -> dict[str, str]:
        return {"X-API-Key": self._options.api_key.reveal()}


class RealtimeCloudServiceCollectionExtensions:
    """Wires the realtime providers a host has consented to."""

    @staticmethod
    def add_realtime_cloud(
        options_by_provider: dict[str, RealtimeCloudOptionsBase],
        connect: Callable[..., RealtimeWebSocketSession] | None = None,
        consented: Sequence[str] = (),
    ) -> list[CloudRealtimeServiceBase]:
        builders = {
            ProviderIds.OPENAI: OpenAiRealtimeService,
            ProviderIds.GEMINI: GeminiLiveService,
            ProviderIds.NOVA_SONIC: NovaSonicService,
            ProviderIds.ELEVENLABS: ElevenLabsConvService,
            ProviderIds.ULTRAVOX: UltravoxService,
        }
        allowed = {p.strip().lower() for p in consented if p and p.strip()}
        out: list[CloudRealtimeServiceBase] = []
        for provider_id, opts in options_by_provider.items():
            builder = builders.get(provider_id.lower())
            if builder is None or provider_id.lower() not in allowed:
                continue
            service = builder(opts, connect)
            if service.is_available:
                out.append(service)
        return out
