"""test_voice_pipeline.py — CircleAI.Voice audio loop.

Covers the null implementations, the streaming energy VAD, the energy wake-word
detector, the VoicePipeline composition (wake -> capture -> [VAD] -> transcribe ->
Transcribed event), and the neural speaker-identity + speech-emotion ports over
injected in-memory model seams. C# (CircleAI.Voice) is the reference.
"""
from __future__ import annotations

import asyncio
import math
import os
import struct
import tempfile
from typing import AsyncIterator, List, Optional

import pytest

from circle_ai.voice import (
    AudioFormat,
    EnergyVadDetector,
    EnergyWakeWordDetector,
    IAudioCapture,
    IEmotionClassifier,
    ISpeakerEmbedder,
    NullAudioCapture,
    NullTtsEngine,
    NullVoiceActivityDetector,
    NullVoiceTranscriber,
    NullWakeWordDetector,
    OnnxSpeakerIdentity,
    OnnxSpeechEmotionDetector,
    PartialTranscription,
    PCM16_MONO_16K,
    SpeakerEmbedderInputKind,
    SpeakerIdentityConfig,
    SpeechEmotionConfig,
    TranscribedEventArgs,
    TranscriptionResult,
    TtsSynthesisResult,
    VadSegment,
    VoicePipeline,
    WakeWordDetectedEventArgs,
)


def _pcm16(*samples: int) -> bytes:
    return struct.pack("<" + "h" * len(samples), *samples)


def _loud_frame(n_samples: int = 320) -> bytes:
    return _pcm16(*([20000, -20000] * (n_samples // 2)))


def _silent_frame(n_samples: int = 320) -> bytes:
    return _pcm16(*([0] * n_samples))


async def _as_stream(chunks: List[bytes]) -> AsyncIterator[bytes]:
    for c in chunks:
        yield c


# ── test doubles ────────────────────────────────────────────────────────────────


class ListAudioCapture(IAudioCapture):
    """Yields a fixed list of PCM chunks then stops."""

    def __init__(self, chunks: List[bytes]) -> None:
        self._chunks = chunks
        self.disposed = False

    @property
    def format(self) -> AudioFormat:
        return PCM16_MONO_16K

    async def capture_async(self, ct: object = None) -> AsyncIterator[bytes]:
        for c in self._chunks:
            yield c
            await asyncio.sleep(0)

    async def dispose_async(self) -> None:
        self.disposed = True


class ScriptedTranscriber:
    """IVoiceTranscriber that returns a fixed transcript for TranscribeAsync and a
    single final PartialTranscription for the stream."""

    def __init__(self, text: str, confidence: float = 0.9) -> None:
        self._text = text
        self._confidence = confidence
        self.disposed = False
        self.stream_calls = 0

    async def transcribe_async(self, pcm_audio: bytes, ct: object = None) -> TranscriptionResult:
        return TranscriptionResult(self._text, self._confidence, "en")

    async def stream_transcribe_async(
        self, audio_chunks: AsyncIterator[bytes], ct: object = None
    ) -> AsyncIterator[PartialTranscription]:
        self.stream_calls += 1
        # Drain the audio (so VAD/capture run) then emit one final result.
        async for _ in audio_chunks:
            pass
        yield PartialTranscription(self._text, True, self._confidence)

    async def dispose_async(self) -> None:
        self.disposed = True


# ── null implementations ────────────────────────────────────────────────────────


async def test_null_audio_capture_yields_nothing():
    cap = NullAudioCapture()
    assert cap.format == PCM16_MONO_16K
    chunks = [c async for c in cap.capture_async()]
    assert chunks == []
    await cap.dispose_async()


async def test_null_transcriber_empty_result_and_drains_stream():
    t = NullVoiceTranscriber()
    res = await t.transcribe_async(_pcm16(1, 2, 3))
    assert res == TranscriptionResult("", 0.0, "und")

    drained = []

    async def producer() -> AsyncIterator[bytes]:
        for c in [_pcm16(1), _pcm16(2)]:
            drained.append(c)
            yield c

    out = [p async for p in t.stream_transcribe_async(producer())]
    assert out == []
    assert len(drained) == 2  # producer fully drained
    await t.dispose_async()


async def test_null_transcriber_raises_after_dispose():
    t = NullVoiceTranscriber()
    await t.dispose_async()
    with pytest.raises(RuntimeError):
        await t.transcribe_async(b"")


async def test_null_wake_word_detector_tracks_state():
    d = NullWakeWordDetector()
    assert d.wake_word == "Hey B"
    assert d.is_listening is False
    await d.start_async()
    assert d.is_listening is True
    await d.stop_async()
    assert d.is_listening is False
    await d.dispose_async()


async def test_null_vad_passthrough():
    vad = NullVoiceActivityDetector()
    segs = [s async for s in vad.detect_async(_as_stream([_pcm16(1), _pcm16(2)]))]
    assert [s.audio for s in segs] == [_pcm16(1), _pcm16(2)]
    assert all(s.is_speech for s in segs)


async def test_null_tts_empty():
    tts = NullTtsEngine()
    res = await tts.synthesise_async("hello")
    assert res == TtsSynthesisResult(b"", 24_000, 1, 16)
    chunks = [c async for c in tts.stream_synthesise_async("hello")]
    assert chunks == []


# ── energy VAD (streaming) ──────────────────────────────────────────────────────


async def test_energy_vad_emits_speech_segment_after_silence_run():
    vad = EnergyVadDetector(energy_threshold=0.05, silence_frames=2, frame_size_bytes=640)
    # 640 bytes = 320 samples/frame. speech, speech, then 2 silence frames -> emit.
    stream = _as_stream([_loud_frame(320), _loud_frame(320), _silent_frame(320), _silent_frame(320)])
    segs = [s async for s in vad.detect_async(stream)]
    assert len(segs) == 1
    assert segs[0].is_speech is True
    # Segment holds the two speech frames + two trailing silence frames.
    assert len(segs[0].audio) == 4 * 640


async def test_energy_vad_flushes_partial_segment_at_stream_end():
    vad = EnergyVadDetector(energy_threshold=0.05, silence_frames=10, frame_size_bytes=640)
    stream = _as_stream([_loud_frame(320), _loud_frame(320)])  # ends mid-speech
    segs = [s async for s in vad.detect_async(stream)]
    assert len(segs) == 1
    assert len(segs[0].audio) == 2 * 640


async def test_energy_vad_handles_split_frames_across_chunks():
    vad = EnergyVadDetector(energy_threshold=0.05, silence_frames=1, frame_size_bytes=640)
    loud = _loud_frame(320)  # 640 bytes
    # Feed the loud frame split into two 320-byte chunks, then a full silent frame.
    stream = _as_stream([loud[:320], loud[320:], _silent_frame(320)])
    segs = [s async for s in vad.detect_async(stream)]
    assert len(segs) == 1
    assert segs[0].is_speech is True


async def test_energy_vad_all_silence_yields_nothing():
    vad = EnergyVadDetector(energy_threshold=0.05, silence_frames=2, frame_size_bytes=640)
    stream = _as_stream([_silent_frame(320), _silent_frame(320), _silent_frame(320)])
    segs = [s async for s in vad.detect_async(stream)]
    assert segs == []


# ── energy wake-word detector ───────────────────────────────────────────────────


async def test_energy_wake_word_fires_on_match():
    cap = ListAudioCapture([_loud_frame(320), _silent_frame(320), _silent_frame(320)])
    transcriber = ScriptedTranscriber("hey b are you there")
    det = EnergyWakeWordDetector(cap, transcriber, wake_word="hey b", energy_threshold=0.05)
    assert det.wake_word == "hey b"

    fired: List[WakeWordDetectedEventArgs] = []
    det.add_wake_word_detected_handler(lambda sender, e: fired.append(e))

    await det.start_async()
    # Let the background loop run to completion (capture is finite).
    for _ in range(50):
        await asyncio.sleep(0)
        if fired:
            break
    await det.stop_async()
    await det.dispose_async()

    assert len(fired) == 1
    assert fired[0].wake_word == "hey b"
    assert fired[0].confidence == pytest.approx(0.9)


async def test_energy_wake_word_no_fire_when_transcript_absent():
    cap = ListAudioCapture([_loud_frame(320), _silent_frame(320), _silent_frame(320)])
    transcriber = ScriptedTranscriber("good morning sunshine")
    det = EnergyWakeWordDetector(cap, transcriber, wake_word="hey b", energy_threshold=0.05)
    fired: List[WakeWordDetectedEventArgs] = []
    det.add_wake_word_detected_handler(lambda sender, e: fired.append(e))
    await det.start_async()
    for _ in range(50):
        await asyncio.sleep(0)
    await det.stop_async()
    await det.dispose_async()
    assert fired == []


async def test_energy_wake_word_start_is_idempotent():
    cap = ListAudioCapture([_silent_frame(320)])
    det = EnergyWakeWordDetector(cap, ScriptedTranscriber(""), wake_word="hey b")
    await det.start_async()
    await det.start_async()  # no throw, no second loop
    await det.stop_async()
    await det.dispose_async()


# ── VoicePipeline ───────────────────────────────────────────────────────────────


async def test_pipeline_fires_transcribed_on_wake():
    # A wake detector we can trigger manually.
    class ManualWake(NullWakeWordDetector):
        def fire(self):
            args = WakeWordDetectedEventArgs(wake_word=self.wake_word, confidence=1.0)
            for h in list(self._handlers):
                h(self, args)

    wake = ManualWake()
    cap = ListAudioCapture([_loud_frame(320), _silent_frame(320)])
    transcriber = ScriptedTranscriber("turn on the lights")
    pipeline = VoicePipeline(wake, transcriber, capture=cap)

    results: List[TranscribedEventArgs] = []
    pipeline.on_transcribed.append(lambda sender, e: results.append(e))

    await pipeline.start_async()
    wake.fire()
    # Allow the scheduled activation task to run.
    for _ in range(50):
        await asyncio.sleep(0)
        if results:
            break
    await pipeline.stop_async()
    await pipeline.dispose_async()

    assert len(results) == 1
    assert results[0].result.text == "turn on the lights"
    assert results[0].result.language_code == "und"  # stream path has no language
    assert transcriber.disposed is True
    assert cap.disposed is True


async def test_pipeline_with_vad_filters_to_speech_only():
    class ManualWake(NullWakeWordDetector):
        def fire(self):
            args = WakeWordDetectedEventArgs(wake_word=self.wake_word, confidence=1.0)
            for h in list(self._handlers):
                h(self, args)

    # Capture: loud (speech) then silence; VAD should forward only speech bytes.
    forwarded: List[int] = []

    class CountingTranscriber(ScriptedTranscriber):
        async def stream_transcribe_async(self, audio_chunks, ct=None):
            self.stream_calls += 1
            total = 0
            async for chunk in audio_chunks:
                total += len(chunk)
            forwarded.append(total)
            yield PartialTranscription(self._text, True, self._confidence)

    wake = ManualWake()
    cap = ListAudioCapture([_loud_frame(320), _silent_frame(320), _silent_frame(320)])
    transcriber = CountingTranscriber("ok")
    vad = EnergyVadDetector(energy_threshold=0.05, silence_frames=2, frame_size_bytes=640)
    pipeline = VoicePipeline(wake, transcriber, capture=cap, vad=vad)

    results: List[TranscribedEventArgs] = []
    pipeline.on_transcribed.append(lambda sender, e: results.append(e))

    await pipeline.start_async()
    wake.fire()
    for _ in range(80):
        await asyncio.sleep(0)
        if results:
            break
    await pipeline.stop_async()
    await pipeline.dispose_async()

    assert len(results) == 1
    # Only speech bytes forwarded (a subset of the 3 frames * 640 bytes).
    assert forwarded and 0 < forwarded[0] <= 3 * 640


async def test_pipeline_activation_failed_event():
    class ManualWake(NullWakeWordDetector):
        def fire(self):
            args = WakeWordDetectedEventArgs(wake_word=self.wake_word, confidence=1.0)
            for h in list(self._handlers):
                h(self, args)

    class BoomTranscriber(ScriptedTranscriber):
        async def stream_transcribe_async(self, audio_chunks, ct=None):
            async for _ in audio_chunks:
                pass
            raise RuntimeError("boom")
            yield  # pragma: no cover

    wake = ManualWake()
    cap = ListAudioCapture([_loud_frame(320)])
    pipeline = VoicePipeline(wake, BoomTranscriber("x"), capture=cap)

    failures: List[Exception] = []
    pipeline.on_activation_failed.append(lambda sender, ex: failures.append(ex))

    await pipeline.start_async()
    wake.fire()
    for _ in range(50):
        await asyncio.sleep(0)
        if failures:
            break
    await pipeline.stop_async()
    await pipeline.dispose_async()

    assert len(failures) == 1
    assert isinstance(failures[0], RuntimeError)


async def test_pipeline_defaults_to_null_capture():
    wake = NullWakeWordDetector()
    pipeline = VoicePipeline(wake, NullVoiceTranscriber())
    assert isinstance(pipeline.audio_capture, NullAudioCapture)
    await pipeline.dispose_async()


async def test_pipeline_stop_after_dispose_raises():
    pipeline = VoicePipeline(NullWakeWordDetector(), NullVoiceTranscriber())
    await pipeline.dispose_async()
    with pytest.raises(RuntimeError):
        await pipeline.start_async()


# ── speaker identity (injected embedder) ────────────────────────────────────────


class HashEmbedder(ISpeakerEmbedder):
    """Deterministic 8-D embedder: bins samples by sign into a small vector so two
    utterances with the same dominant polarity land near each other in cosine
    space. Enough to exercise enroll/identify without a real model."""

    def _embed(self, values: List[float]) -> List[float]:
        acc = [0.0] * 8
        for i, v in enumerate(values):
            acc[i % 8] += v
        return acc

    def embed_waveform(self, window: List[float]) -> List[float]:
        return self._embed(window)

    def embed_log_mel(self, log_mel: List[List[float]]) -> List[float]:
        flat = [x for row in log_mel for x in row]
        return self._embed(flat)


def _speaker_config(tmp: str, kind=SpeakerEmbedderInputKind.RAW_WAVEFORM) -> SpeakerIdentityConfig:
    return SpeakerIdentityConfig(
        model_path=os.path.join(tmp, "model.onnx"),
        enrollment_store_path=os.path.join(tmp, "enroll.json"),
        input_kind=kind,
        min_utterance_ms=10,   # tiny utterances OK for the test
        max_utterance_ms=8_000,
        match_threshold=0.5,
    )


async def test_speaker_identify_empty_before_enroll():
    with tempfile.TemporaryDirectory() as tmp:
        ident = OnnxSpeakerIdentity(_speaker_config(tmp), HashEmbedder())
        # 16k * 10ms = 160 samples min; give 200 samples.
        audio = _pcm16(*([15000, -15000] * 100))
        assert await ident.identify_async(audio, 16_000) is None
        await ident.dispose_async()


async def test_speaker_enroll_then_identify_roundtrip():
    with tempfile.TemporaryDirectory() as tmp:
        ident = OnnxSpeakerIdentity(_speaker_config(tmp), HashEmbedder())
        pos = _pcm16(*([15000] * 200))   # all-positive utterance
        await ident.enroll_async("alice", pos, 16_000)
        # An enrollment store file should now exist.
        assert os.path.isfile(_speaker_config(tmp).enrollment_store_path)
        who = await ident.identify_async(_pcm16(*([14000] * 200)), 16_000)
        assert who == "alice"
        await ident.dispose_async()


async def test_speaker_enroll_averages_and_persists():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = _speaker_config(tmp)
        ident = OnnxSpeakerIdentity(cfg, HashEmbedder())
        await ident.enroll_async("bob", _pcm16(*([10000] * 200)), 16_000)
        await ident.enroll_async("bob", _pcm16(*([12000] * 200)), 16_000)
        await ident.dispose_async()

        # Reload from disk — the persisted centroid must survive.
        ident2 = OnnxSpeakerIdentity(cfg, HashEmbedder())
        who = await ident2.identify_async(_pcm16(*([11000] * 200)), 16_000)
        assert who == "bob"
        await ident2.dispose_async()


async def test_speaker_short_utterance_rejected_for_identify():
    with tempfile.TemporaryDirectory() as tmp:
        ident = OnnxSpeakerIdentity(_speaker_config(tmp), HashEmbedder())
        await ident.enroll_async("carol", _pcm16(*([9000] * 200)), 16_000)
        # 5 samples << 160-sample minimum -> embedding None -> no match.
        assert await ident.identify_async(_pcm16(1, 2, 3, 4, 5), 16_000) is None
        await ident.dispose_async()


async def test_speaker_wrong_sample_rate_rejected():
    with tempfile.TemporaryDirectory() as tmp:
        ident = OnnxSpeakerIdentity(_speaker_config(tmp), HashEmbedder())
        with pytest.raises(RuntimeError):
            await ident.enroll_async("dave", _pcm16(*([9000] * 200)), 8_000)
        await ident.dispose_async()


async def test_speaker_log_mel_input_kind_path():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = _speaker_config(tmp, kind=SpeakerEmbedderInputKind.LOG_MEL)
        ident = OnnxSpeakerIdentity(cfg, HashEmbedder())
        await ident.enroll_async("erin", _pcm16(*([13000] * 400)), 16_000)
        who = await ident.identify_async(_pcm16(*([13000] * 400)), 16_000)
        assert who == "erin"
        await ident.dispose_async()


# ── speech emotion (injected classifier) ────────────────────────────────────────


class PickIndexClassifier(IEmotionClassifier):
    """Returns logits that make ``target`` the argmax."""

    def __init__(self, target: int, n_classes: int = 4) -> None:
        self._target = target
        self._n = n_classes

    def classify(self, window: List[float]) -> List[float]:
        logits = [0.0] * self._n
        logits[self._target] = 10.0
        return logits


async def test_emotion_maps_label_and_circumplex():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = SpeechEmotionConfig(model_path=os.path.join(tmp, "m.onnx"))
        det = OnnxSpeechEmotionDetector(cfg, PickIndexClassifier(2))  # "angry"
        frame = await det.sense_async(_pcm16(*([1000] * 100)), 16_000)
        assert frame is not None
        assert frame.label == "angry"
        assert frame.arousal == pytest.approx(0.74)
        assert frame.valence == pytest.approx(-0.62)
        assert frame.probability > 0.9
        await det.dispose_async()


async def test_emotion_empty_audio_returns_none():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = SpeechEmotionConfig(model_path=os.path.join(tmp, "m.onnx"))
        det = OnnxSpeechEmotionDetector(cfg, PickIndexClassifier(0))
        assert await det.sense_async(b"", 16_000) is None
        await det.dispose_async()


async def test_emotion_wrong_sample_rate_returns_none():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = SpeechEmotionConfig(model_path=os.path.join(tmp, "m.onnx"))
        det = OnnxSpeechEmotionDetector(cfg, PickIndexClassifier(1))
        assert await det.sense_async(_pcm16(*([1000] * 100)), 8_000) is None
        await det.dispose_async()


async def test_emotion_custom_labels():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = SpeechEmotionConfig(model_path=os.path.join(tmp, "m.onnx"), labels=["calm", "excited"])
        det = OnnxSpeechEmotionDetector(cfg, PickIndexClassifier(1, n_classes=2))
        frame = await det.sense_async(_pcm16(*([1000] * 100)), 16_000)
        assert frame is not None
        assert frame.label == "excited"
        assert frame.arousal == pytest.approx(0.82)
        await det.dispose_async()


async def test_emotion_unknown_label_maps_to_neutral_coords():
    with tempfile.TemporaryDirectory() as tmp:
        cfg = SpeechEmotionConfig(model_path=os.path.join(tmp, "m.onnx"), labels=["xyzzy"])
        det = OnnxSpeechEmotionDetector(cfg, PickIndexClassifier(0, n_classes=1))
        frame = await det.sense_async(_pcm16(*([1000] * 100)), 16_000)
        assert frame is not None
        assert frame.label == "xyzzy"
        assert (frame.arousal, frame.valence) == (0.0, 0.0)
        await det.dispose_async()
