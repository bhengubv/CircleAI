# video/primitives.py
#
# Port of CircleAI.Video/Primitives.cs (C# — the EXACT spec).
#
# (3.1.0) Shared shapes for the video contract surface.
#
# C# -> Python mapping:
#   ReadOnlyMemory<byte>          -> bytes
#   readonly record struct        -> @dataclass(frozen=True, slots=True)
#   sealed record                 -> @dataclass(frozen=True, slots=True)
#   IReadOnlyList<T>              -> tuple[T, ...]
#   TimeSpan                      -> datetime.timedelta
#   long?                         -> Optional[int]

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta
from typing import Optional, Tuple


@dataclass(frozen=True, slots=True)
class StyleId:
    """Identifier for one registered style (e.g. "pooh-1926", "noir-detective",
    "space-opera").

    Mirrors ``CircleAI.Video.StyleId`` — ``readonly record struct StyleId(string Value)``.
    The C# ``ToString()`` override + ``implicit operator string`` both surface the
    inner value; here ``str(style_id)`` returns ``value``.
    """

    value: str

    def __str__(self) -> str:
        return self.value


@dataclass(frozen=True, slots=True)
class VideoResolution:
    """Output resolution for a generated video.

    Mirrors ``CircleAI.Video.VideoResolution`` — ``readonly record struct
    VideoResolution(int Width, int Height)`` with the P480/P720/P1080 presets.
    The presets are attached as class attributes below (see ``VideoResolution.P480``).
    """

    width: int
    height: int


# C# static factory properties ``VideoResolution.P480 => new(720, 480)`` etc.
# Attached as class attributes so callers use ``VideoResolution.P480``.
VideoResolution.P480 = VideoResolution(720, 480)  # type: ignore[attr-defined]
VideoResolution.P720 = VideoResolution(1280, 720)  # type: ignore[attr-defined]
VideoResolution.P1080 = VideoResolution(1920, 1080)  # type: ignore[attr-defined]


@dataclass(frozen=True, slots=True)
class StyleReferenceFrame:
    """One reference frame the generator can ground style on — public-domain
    illustration, original-character render, etc.

    Mirrors ``CircleAI.Video.StyleReferenceFrame``.
    """

    image_bytes: bytes
    mime_type: str
    caption: Optional[str] = None


@dataclass(frozen=True, slots=True)
class StyleAttribution:
    """Attribution + license metadata for one style. Lets txtMe (and any other
    consumer) display the source to the user before rendering.

    Mirrors ``CircleAI.Video.StyleAttribution``.
    """

    source: str
    license: str
    url: Optional[str] = None


@dataclass(frozen=True, slots=True)
class StyleReference:
    """One style the host has registered with the catalogue. Picked up by
    ``IStyleReference.get_async(style_id)``.

    Mirrors ``CircleAI.Video.StyleReference``.
    """

    id: StyleId
    display_name: str
    short_description: str
    attribution: StyleAttribution
    voice_persona_id: Optional[str]
    frames: Tuple[StyleReferenceFrame, ...]


@dataclass(frozen=True, slots=True)
class AudioTrack:
    """Audio track produced by CircleAI.Speech for the generator to embed.

    Mirrors ``CircleAI.Video.AudioTrack``.
    """

    audio_pcm16_mono: bytes
    sample_rate_hz: int
    duration: timedelta


@dataclass(frozen=True, slots=True)
class VideoGenerationRequest:
    """One generation request — text + optional style + optional grounding image
    + optional audio.

    Mirrors ``CircleAI.Video.VideoGenerationRequest``.
    """

    prompt: str
    duration: timedelta
    resolution: VideoResolution
    frame_rate: int = 24
    style_id: Optional[StyleId] = None
    reference_image: Optional[StyleReferenceFrame] = None
    audio_track: Optional[AudioTrack] = None
    seed: Optional[int] = None


@dataclass(frozen=True, slots=True)
class VideoGenerationResult:
    """One generation outcome.

    Mirrors ``CircleAI.Video.VideoGenerationResult``.
    """

    video_bytes: bytes
    mime_type: str
    duration: timedelta
    frame_count: int
    resolution: VideoResolution
    backend_id: str


@dataclass(frozen=True, slots=True)
class StyleScriptRequest:
    """One style-script request — raw user message + chosen voice.

    Mirrors ``CircleAI.Video.StyleScriptRequest``.
    """

    source_message: str
    style: StyleId
    speaker_hint: Optional[str] = None
    language_hint: Optional[str] = None


@dataclass(frozen=True, slots=True)
class StyleScriptResult:
    """One style-script outcome — the rewritten line + voice + estimated duration.

    Mirrors ``CircleAI.Video.StyleScriptResult``.
    """

    rewritten_text: str
    style: StyleId
    voice_persona_id: Optional[str]
    estimated_spoken_duration: timedelta


__all__ = [
    "StyleId",
    "VideoResolution",
    "StyleReferenceFrame",
    "StyleAttribution",
    "StyleReference",
    "AudioTrack",
    "VideoGenerationRequest",
    "VideoGenerationResult",
    "StyleScriptRequest",
    "StyleScriptResult",
]
