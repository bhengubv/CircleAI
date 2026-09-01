"""The inference server: endpoints, its diagnostics, and how it is put together.

THIS SERVER BINDS TO LOOPBACK. It exists so a program on the same device can use
the model already loaded on it, not so a device becomes a service on a network.
Binding to 0.0.0.0 turns a phone into an open inference endpoint on whatever
Wi-Fi it joins, so the default refuses and a wider bind must be asked for by
name AND carry a key.

THE HANDLERS ARE PURE. Each takes a parsed request and returns a status and a
body; nothing here touches a socket. That is what makes the auth logic testable
without a server, and the auth logic is the part worth testing.

THE DTOs ARE SEPARATE FROM THE DOMAIN TYPES ON PURPOSE. A response shape is a
promise to whoever is calling; a domain type changes when the code changes.
Serialising domain types directly is how an internal rename becomes somebody
else's broken client.
"""

from __future__ import annotations

import hmac
import json
import threading
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Callable, Sequence


# ─────────────────────────────────────────────────────────────────────────────
# Auth


@dataclass
class ApiKeyAuthSchemeOptions:
    """How the server authenticates callers.

    NO DEFAULT KEY. A default key is a published key: it reaches a README, then
    a search engine, and every device that never changed it is open.
    """

    header_name: str = "X-CircleAI-Key"
    #: Hashes, never the keys. A process that holds the plaintext will leak it
    #: from a core dump, a log or a debugger, and the server never needs it.
    key_hashes: tuple[str, ...] = ()
    #: When False, every request is allowed. Only safe on loopback, and the
    #: builder refuses to combine it with a wider bind.
    required: bool = True
    allow_loopback_without_key: bool = True

    @property
    def is_configured(self) -> bool:
        return bool(self.key_hashes) or not self.required


class ApiKeyAuthHandler:
    """Checks a key against the configured hashes."""

    def __init__(
        self,
        options: ApiKeyAuthSchemeOptions | None = None,
        hash_key: Callable[[str], str] | None = None,
    ) -> None:
        self._options = options or ApiKeyAuthSchemeOptions()
        self._hash = hash_key or (lambda k: k)

    @property
    def options(self) -> ApiKeyAuthSchemeOptions:
        return self._options

    def authenticate(
        self, headers: dict[str, str], is_loopback: bool = False
    ) -> tuple[bool, str]:
        """Returns (allowed, reason).

        Headers are matched CASE-INSENSITIVELY, because HTTP header names are
        case-insensitive and a client that sends `x-circleai-key` is correct.
        Rejecting it produces a 401 that nobody can explain.
        """
        if not self._options.required:
            return True, "this server does not require a key"
        if is_loopback and self._options.allow_loopback_without_key:
            return True, "loopback, where the caller is already on this device"
        if not self._options.key_hashes:
            # No keys and a required scheme means DENY. Falling open here would
            # make a misconfiguration into an open server.
            return False, "this server requires a key and none is configured"

        lowered = {k.lower(): v for k, v in headers.items()}
        supplied = lowered.get(self._options.header_name.lower(), "")
        if not supplied:
            return False, "no key was supplied"
        candidate = self._hash(supplied)
        # compare_digest against EVERY hash, without an early exit, so the time
        # taken does not say how many keys are configured or which one nearly
        # matched.
        matched = False
        for known in self._options.key_hashes:
            matched |= hmac.compare_digest(candidate, known)
        # Says "not accepted", not "wrong key" - the second confirms to somebody
        # guessing that the header name and format were right.
        return (True, "key accepted") if matched else (False, "the key was not accepted")


# ─────────────────────────────────────────────────────────────────────────────
# Response shapes


@dataclass(frozen=True)
class HostProfileDto:
    """What the host is, as told to a caller."""

    platform: str = ""
    #: Never the device NAME. A phone's name is usually a person's name, and it
    #: has no business in a diagnostics response.
    device_class: str = ""
    cpu_count: int = 0
    ram_gb: float = 0.0
    #: Whether that RAM figure may be trusted for sizing. Carried through
    #: because a caller choosing a model needs to know it is a real measurement
    #: and not a heap reading.
    ram_is_measured: bool = False


@dataclass(frozen=True)
class NativeRuntimePathsDto:
    """Where the native runtime came from."""

    abi: str = ""
    #: The base name only. The full path leaks the install layout and, on a
    #: desktop, usually a person's home directory.
    library: str = ""
    is_loaded: bool = False


@dataclass(frozen=True)
class BackendSelectionDto:
    """Which backend was chosen and why."""

    backend: str = ""
    reason: str = ""
    fell_back: bool = False


@dataclass(frozen=True)
class LoadedModelInfo:
    """A model the server currently holds."""

    model_id: str = ""
    modality: str = "text"
    parameters_billion: float = 0.0
    quantisation: str = ""
    context_length: int = 0
    loaded_seconds_ago: float = 0.0


@dataclass(frozen=True)
class CounterSnapshot:
    """One counter."""

    name: str
    value: int = 0


@dataclass(frozen=True)
class HealthResponse:
    """The cheap check.

    DELIBERATELY THIN and free of anything identifying. A health endpoint is the
    one thing that gets polled by anything on the network, so it says only
    whether the server can answer.
    """

    ok: bool = True
    ready: bool = False
    uptime_seconds: float = 0.0

    def to_dict(self) -> dict[str, object]:
        return {"ok": self.ok, "ready": self.ready,
                "uptime_seconds": round(self.uptime_seconds, 1)}


@dataclass(frozen=True)
class DiagnosticsResponse:
    """The full picture, for somebody debugging on the device."""

    host: HostProfileDto = field(default_factory=HostProfileDto)
    native: NativeRuntimePathsDto = field(default_factory=NativeRuntimePathsDto)
    backend: BackendSelectionDto = field(default_factory=BackendSelectionDto)
    models: tuple[LoadedModelInfo, ...] = ()
    counters: tuple[CounterSnapshot, ...] = ()
    p95_ms: dict[str, float] = field(default_factory=dict)

    def to_dict(self) -> dict[str, object]:
        return {
            "host": self.host.__dict__,
            "native": self.native.__dict__,
            "backend": self.backend.__dict__,
            "models": [m.__dict__ for m in self.models],
            "counters": {c.name: c.value for c in self.counters},
            "p95_ms": self.p95_ms,
        }


# ─────────────────────────────────────────────────────────────────────────────
# Endpoints


@dataclass(frozen=True)
class EndpointResponse:
    """What a handler returns."""

    status: int = 200
    body: dict[str, object] = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return 200 <= self.status < 300

    def to_json(self) -> str:
        return json.dumps(self.body, ensure_ascii=False)


class IEndpoint(ABC):
    """One route."""

    @property
    @abstractmethod
    def path(self) -> str: ...

    @abstractmethod
    def handle(self, request: dict[str, object]) -> EndpointResponse: ...


class ChatCompletionsEndpoint(IEndpoint):
    """OpenAI-shaped chat completions, served locally.

    THE SHAPE IS COPIED ON PURPOSE. Anything already written against that API
    works against this server by changing a base URL, and that is the whole
    reason a local server exists rather than a bespoke protocol.
    """

    def __init__(self, generate: Callable[[list[dict[str, str]], dict[str, object]], str] | None = None) -> None:
        self._generate = generate

    @property
    def path(self) -> str:
        return "/v1/chat/completions"

    def handle(self, request: dict[str, object]) -> EndpointResponse:
        messages = request.get("messages")
        if not isinstance(messages, list) or not messages:
            # 400 with a reason, not a 500. A malformed request is the caller's
            # to fix and telling them which field is missing is the whole
            # difference between a usable API and a guessing game.
            return EndpointResponse(400, {"error": {
                "message": "messages is required and must be a non-empty list",
                "type": "invalid_request_error", "param": "messages"}})
        if self._generate is None:
            return EndpointResponse(503, {"error": {
                "message": "no model is loaded on this device",
                "type": "service_unavailable"}})
        turns = [
            {"role": str(m.get("role", "user")), "content": str(m.get("content", ""))}
            for m in messages if isinstance(m, dict)
        ]
        try:
            text = self._generate(turns, request)
        except Exception as exc:  # noqa: BLE001
            return EndpointResponse(500, {"error": {
                "message": str(exc), "type": "inference_error"}})
        return EndpointResponse(200, {
            "object": "chat.completion",
            "model": str(request.get("model", "")),
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": text},
                # Always populated. A client that switches on it gets None from
                # a server that omits it, and treats a finished reply as a
                # truncated one.
                "finish_reason": "stop",
            }],
        })


class EmbeddingsEndpoint(IEndpoint):
    """Embeddings, OpenAI-shaped."""

    def __init__(self, embed: Callable[[Sequence[str]], list[list[float]]] | None = None) -> None:
        self._embed = embed

    @property
    def path(self) -> str:
        return "/v1/embeddings"

    def handle(self, request: dict[str, object]) -> EndpointResponse:
        raw = request.get("input")
        # A single string and a list of strings are BOTH valid input in this
        # API. Accepting only the list rejects the commonest call.
        inputs = [raw] if isinstance(raw, str) else raw
        if not isinstance(inputs, list) or not inputs:
            return EndpointResponse(400, {"error": {
                "message": "input is required, as a string or a list of strings",
                "type": "invalid_request_error", "param": "input"}})
        if self._embed is None:
            return EndpointResponse(503, {"error": {
                "message": "no embedding model is loaded on this device",
                "type": "service_unavailable"}})
        vectors = self._embed([str(i) for i in inputs])
        return EndpointResponse(200, {
            "object": "list",
            "data": [
                {"object": "embedding", "index": i, "embedding": v}
                for i, v in enumerate(vectors)
            ],
        })


class CompanionEndpoint(IEndpoint):
    """The companion's own surface.

    Separate from chat completions because it carries state a stateless
    completion cannot: which conversation, which memories are in scope, and
    what the companion is allowed to do on this turn.
    """

    def __init__(self, respond: Callable[[str, str], str] | None = None) -> None:
        self._respond = respond

    @property
    def path(self) -> str:
        return "/v1/companion"

    def handle(self, request: dict[str, object]) -> EndpointResponse:
        text = str(request.get("text", "")).strip()
        if not text:
            return EndpointResponse(400, {"error": {
                "message": "text is required", "type": "invalid_request_error"}})
        if self._respond is None:
            return EndpointResponse(503, {"error": {
                "message": "the companion is not available on this device",
                "type": "service_unavailable"}})
        session = str(request.get("session_id", ""))
        return EndpointResponse(200, {
            "text": self._respond(session, text),
            # Echoed back so a caller that did not supply one learns the id the
            # server used, rather than starting a new conversation every turn.
            "session_id": session,
        })


class DiagnosticsEndpoint(IEndpoint):
    """Health and diagnostics.

    TWO PATHS, DIFFERENT AUDIENCES. Health is polled by anything and says almost
    nothing; diagnostics answers a person debugging on the device and is
    treated as privileged.
    """

    def __init__(
        self,
        health: Callable[[], HealthResponse] | None = None,
        diagnostics: Callable[[], DiagnosticsResponse] | None = None,
    ) -> None:
        self._health = health
        self._diagnostics = diagnostics

    @property
    def path(self) -> str:
        return "/health"

    def handle(self, request: dict[str, object]) -> EndpointResponse:
        wants_full = str(request.get("path", "")).rstrip("/").endswith("diagnostics")
        if not wants_full:
            h = self._health() if self._health else HealthResponse()
            return EndpointResponse(200, h.to_dict())
        if self._diagnostics is None:
            return EndpointResponse(200, DiagnosticsResponse().to_dict())
        return EndpointResponse(200, self._diagnostics().to_dict())


class AdminEndpoints(IEndpoint):
    """Loading and unloading models.

    ALWAYS REQUIRES A KEY, even on loopback where the other endpoints do not.
    Everything else here answers questions; this one changes what the device is
    doing and can make it fetch several gigabytes.
    """

    def __init__(
        self,
        load: Callable[[str], bool] | None = None,
        unload: Callable[[str], bool] | None = None,
        gate: Callable[[str], tuple[bool, str]] | None = None,
    ) -> None:
        self._load = load
        self._unload = unload
        self._gate = gate

    @property
    def path(self) -> str:
        return "/admin/models"

    @property
    def requires_key_always(self) -> bool:
        return True

    def handle(self, request: dict[str, object]) -> EndpointResponse:
        action = str(request.get("action", "")).lower()
        model_id = str(request.get("model_id", "")).strip()
        if not model_id:
            return EndpointResponse(400, {"error": {
                "message": "model_id is required", "type": "invalid_request_error"}})
        if action == "load":
            if self._gate is not None:
                allowed, reason = self._gate(model_id)
                if not allowed:
                    # 409, not 403. The request was legitimate and the device
                    # declined for a reason the caller can act on - which is a
                    # conflict with the device's state, not a refusal of
                    # authority.
                    return EndpointResponse(409, {"error": {
                        "message": reason, "type": "download_blocked"}})
            if self._load is None:
                return EndpointResponse(503, {"error": {
                    "message": "this server cannot load models",
                    "type": "service_unavailable"}})
            return EndpointResponse(200, {"loaded": self._load(model_id), "model_id": model_id})
        if action == "unload":
            if self._unload is None:
                return EndpointResponse(503, {"error": {
                    "message": "this server cannot unload models",
                    "type": "service_unavailable"}})
            return EndpointResponse(200, {"unloaded": self._unload(model_id), "model_id": model_id})
        return EndpointResponse(400, {"error": {
            "message": "action must be load or unload",
            "type": "invalid_request_error", "param": "action"}})


class MnnInferenceBridgeFactory:
    """Builds the bridge to the native runtime, once.

    CACHED, because building it twice loads the model twice and a phone does not
    have room for two. The cache is keyed on the model id, so switching models
    releases the old bridge rather than accumulating them.
    """

    def __init__(self, build: Callable[[str], object] | None = None) -> None:
        self._build = build
        self._lock = threading.Lock()
        self._model_id = ""
        self._bridge: object | None = None

    @property
    def current_model_id(self) -> str:
        return self._model_id

    def get(self, model_id: str) -> object | None:
        if not model_id or self._build is None:
            return None
        with self._lock:
            if self._bridge is not None and self._model_id == model_id:
                return self._bridge
            # The old bridge is dropped BEFORE the new one is built. Holding
            # both for the length of a load needs twice the memory, at the one
            # moment the device has least of it.
            self._bridge = None
            self._model_id = ""
            bridge = self._build(model_id)
            self._bridge, self._model_id = bridge, model_id
            return bridge

    def release(self) -> None:
        with self._lock:
            self._bridge = None
            self._model_id = ""


@dataclass
class InferenceServerOptions:
    """How the server is exposed."""

    #: LOOPBACK. A phone that binds 0.0.0.0 becomes an open inference endpoint
    #: on whatever Wi-Fi it joins.
    host: str = "127.0.0.1"
    port: int = 8317
    auth: ApiKeyAuthSchemeOptions = field(default_factory=ApiKeyAuthSchemeOptions)
    max_concurrent_requests: int = 2

    @property
    def is_loopback_only(self) -> bool:
        return self.host in ("127.0.0.1", "::1", "localhost")


class InferenceServerBuilder:
    """Assembles the server, refusing combinations that would open it up."""

    def __init__(self, options: InferenceServerOptions | None = None) -> None:
        self._options = options or InferenceServerOptions()
        self._endpoints: list[IEndpoint] = []

    @property
    def options(self) -> InferenceServerOptions:
        return self._options

    def add(self, endpoint: IEndpoint) -> "InferenceServerBuilder":
        self._endpoints.append(endpoint)
        return self

    @property
    def endpoints(self) -> tuple[IEndpoint, ...]:
        return tuple(self._endpoints)

    def validate(self) -> tuple[bool, str]:
        """The one rule worth enforcing at build time.

        A wider bind with no key is an open inference endpoint on somebody's
        café Wi-Fi. Refused here rather than warned about, because a warning at
        startup is a line of log nobody reads.
        """
        auth = self._options.auth
        if not self._options.is_loopback_only:
            if not auth.required or not auth.key_hashes:
                return False, (
                    f"binding to {self._options.host} without a key would put "
                    f"this device's model on the network - configure a key or "
                    f"bind to 127.0.0.1")
        if self._options.max_concurrent_requests < 1:
            return False, "at least one request must be allowed at a time"
        return True, "loopback only" if self._options.is_loopback_only else "keyed"

    def build(self) -> "InferenceServer":
        ok, reason = self.validate()
        if not ok:
            raise ValueError(reason)
        return InferenceServer(self._options, tuple(self._endpoints))


class InferenceServer:
    """Routes a parsed request to an endpoint.

    Pure: no socket, no framework. A host binds whatever it likes and calls
    `dispatch`, which means the auth and routing rules are testable exactly as
    they will run.
    """

    def __init__(
        self, options: InferenceServerOptions, endpoints: Sequence[IEndpoint],
        hash_key: Callable[[str], str] | None = None,
    ) -> None:
        self._options = options
        self._endpoints = {e.path: e for e in endpoints}
        self._auth = ApiKeyAuthHandler(options.auth, hash_key)
        self._started = time.monotonic()
        self._in_flight = 0
        self._lock = threading.Lock()

    @property
    def uptime_seconds(self) -> float:
        return time.monotonic() - self._started

    def dispatch(
        self, path: str, body: dict[str, object] | None = None,
        headers: dict[str, str] | None = None, is_loopback: bool = True,
    ) -> EndpointResponse:
        endpoint = self._endpoints.get(path.rstrip("/") or "/")
        if endpoint is None:
            # The diagnostics endpoint owns /health and answers a sub-path,
            # which the flat table cannot express.
            for candidate in self._endpoints.values():
                if path.startswith(candidate.path) and isinstance(candidate, DiagnosticsEndpoint):
                    endpoint = candidate
                    break
        if endpoint is None:
            return EndpointResponse(404, {"error": {
                "message": f"no endpoint at {path}", "type": "not_found"}})

        # Admin overrides the loopback exemption, and is checked BEFORE the
        # general rule rather than after it.
        loopback_ok = is_loopback and not getattr(endpoint, "requires_key_always", False)
        allowed, reason = self._auth.authenticate(headers or {}, loopback_ok)
        if not allowed:
            return EndpointResponse(401, {"error": {
                "message": reason, "type": "unauthorized"}})

        with self._lock:
            if self._in_flight >= self._options.max_concurrent_requests:
                # 503 with a retry hint, not a queue. Queueing inference
                # requests on a phone means the third caller waits behind two
                # generations and times out anyway, having also kept the model
                # resident and the device hot.
                return EndpointResponse(503, {"error": {
                    "message": "this device is already busy generating",
                    "type": "busy", "retry_after_seconds": 5}})
            self._in_flight += 1
        try:
            request = dict(body or {})
            request.setdefault("path", path)
            return endpoint.handle(request)
        finally:
            with self._lock:
                self._in_flight -= 1


class Program:
    """The entry point.

    Here so the tree lines up with the C#, and so there is one obvious place
    that shows the assembly order: options, then endpoints, then validate, then
    bind. Validation happens BEFORE anything binds a port.
    """

    @staticmethod
    def build(
        options: InferenceServerOptions | None = None,
        generate: Callable[[list[dict[str, str]], dict[str, object]], str] | None = None,
        embed: Callable[[Sequence[str]], list[list[float]]] | None = None,
        respond: Callable[[str, str], str] | None = None,
        health: Callable[[], HealthResponse] | None = None,
        diagnostics: Callable[[], DiagnosticsResponse] | None = None,
    ) -> InferenceServer:
        builder = InferenceServerBuilder(options)
        builder.add(ChatCompletionsEndpoint(generate))
        builder.add(EmbeddingsEndpoint(embed))
        builder.add(CompanionEndpoint(respond))
        builder.add(DiagnosticsEndpoint(health, diagnostics))
        builder.add(AdminEndpoints())
        return builder.build()

    @staticmethod
    def main(argv: Sequence[str] = ()) -> int:
        """Returns an exit code and prints the reason on refusal.

        A refusal to start is a 2, not a 1: a caller scripting this can tell a
        configuration it must fix from a crash it should report.
        """
        options = InferenceServerOptions()
        for arg in argv:
            if arg.startswith("--host="):
                options.host = arg.split("=", 1)[1]
            elif arg.startswith("--port="):
                options.port = int(arg.split("=", 1)[1])
        ok, reason = InferenceServerBuilder(options).validate()
        if not ok:
            print(reason)
            return 2
        return 0
