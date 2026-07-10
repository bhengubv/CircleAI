"""test_speech_contracts.py — CircleAI.Speech contract surface + implementations.

Covers the null (fail-closed) defaults, the deterministic keyword recognizer /
template synthesizer / keyword wake-word detector, the pure-DSP echo cancellers,
noise reducers, VADs, end-of-turn detectors, and the G.711 / resample audio
converter. C# (CircleAI.Speech) is the reference.
"""
from __future__ import annotations

import struct
from datetime import timedelta

import pytest

from circle_ai.speech import (
    AudioCodec,
    AudioFormatConverter,
    DeepFilterNetNoiseReducer,
    EndOfTurnResult,
    EnergyVoiceActivityDetector,
    IEchoCancellerModelRunner,
    INoiseReducerModelRunner,
    IVadModelRunner,
    ITurnModelRunner,
    KeywordSpeechRecognizer,
    KeywordWakeWordDetector,
    KrispNoiseReducer,
    NlmsEchoCanceller,
    NullEchoCanceller,
    NullEndOfTurnDetector,
    NullNoiseReducer,
    NullOpticalCharacterRecognizer,
    NullSpeechRecognizer,
    NullSpeechSynthesizer,
    NullVoiceActivityDetector,
    NullWakeWordDetector,
    RuleBasedEndOfTurnDetector,
    SileroVoiceActivityDetector,
    SmartTurnDetector,
    SpectralSubtractionNoiseReducer,
    TemplateSpeechSynthesizer,
    TranscriptionResult,
    VadFrameResult,
    WakeWordEvent,
    WebRtcEchoCanceller,
    decode_a_law_to_pcm16,
    decode_mu_law_to_pcm16,
    encode_pcm16_to_a_law,
    encode_pcm16_to_mu_law,
    resample_pcm16_linear,
)


def _pcm16(*samples: int) -> bytes:
    return struct.pack("<" + "h" * len(samples), *samples)


def _shorts(buf: bytes) -> tuple:
    return struct.unpack("<" + "h" * (len(buf) // 2), buf)


# ── null implementations ────────────────────────────────────────────────────────


async def test_null_speech_recognizer_returns_empty():
    r = NullSpeechRecognizer.instance()
    assert r.backend_id == "null"
    res = await r.transcribe_async(_pcm16(1, 2, 3), 16_000, "en")
    assert res.text == ""
    assert res.language == "en"
    assert res.segments == ()
    assert res.total_duration == timedelta(0)


async def test_null_speech_synthesizer_returns_empty():
    s = NullSpeechSynthesizer.instance()
    assert s.backend_id == "null"
    res = await s.synthesize_async("hello")
    assert res.audio_pcm16_mono == b""
    assert res.sample_rate_hz == 16_000
    assert res.duration == timedelta(0)


async def test_null_ocr_returns_empty():
    o = NullOpticalCharacterRecognizer.instance()
    assert o.backend_id == "null"
    res = await o.recognize_async(b"\x00\x01")
    assert res.text == ""
    assert res.blocks == ()


async def test_null_wake_word_detector_never_fires():
    fired = []
    det = NullWakeWordDetector()
    assert det.backend_id == "null"
    sub = det.subscribe(lambda e: fired.append(e))
    await det.start_async()
    await det.stop_async()
    sub.dispose()
    await det.dispose_async()
    assert fired == []


# ── deterministic keyword recognizer ────────────────────────────────────────────


async def test_keyword_recognizer_utf8_passthrough_and_duration():
    r = KeywordSpeechRecognizer()
    assert r.backend_id == "keyword"
    audio = "hello world".encode("utf-8")
    res = await r.transcribe_async(audio, 16_000)
    assert res.text == "hello world"
    assert res.language == "en"
    assert len(res.segments) == 1
    # duration derived from bytes as PCM-16: (11 bytes // 2) samples / 16000.
    expected = (len(audio) // 2) / 16_000
    assert res.total_duration == timedelta(seconds=expected)
    assert res.segments[0].confidence == 1.0


async def test_keyword_recognizer_maps_keyword_to_canonical():
    r = KeywordSpeechRecognizer(keyword_map={"lights": "turn on the lights"})
    res = await r.transcribe_async("please the LIGHTS now".encode("utf-8"), 16_000)
    assert res.text == "turn on the lights"


async def test_keyword_recognizer_empty_audio():
    r = KeywordSpeechRecognizer()
    res = await r.transcribe_async(b"", 16_000)
    assert res.text == ""
    assert res.segments == ()


async def test_keyword_recognizer_language_hint_overrides_default():
    r = KeywordSpeechRecognizer()
    res = await r.transcribe_async("bonjour".encode("utf-8"), 16_000, language_hint="fr")
    assert res.language == "fr"


# ── deterministic template synthesizer ──────────────────────────────────────────


async def test_template_synthesizer_length_is_pure_function_of_text():
    s = TemplateSpeechSynthesizer(sample_rate_hz=16_000, samples_per_char=100)
    assert s.backend_id == "template"
    res = await s.synthesize_async("abc")
    # 3 chars * 100 samples/char * 2 bytes = 600 bytes.
    assert len(res.audio_pcm16_mono) == 3 * 100 * 2
    assert res.sample_rate_hz == 16_000
    assert res.duration == timedelta(seconds=(3 * 100) / 16_000)


async def test_template_synthesizer_deterministic():
    s = TemplateSpeechSynthesizer(samples_per_char=10)
    a = await s.synthesize_async("hi there")
    b = await s.synthesize_async("hi there")
    assert a.audio_pcm16_mono == b.audio_pcm16_mono


async def test_template_synthesizer_empty_text():
    s = TemplateSpeechSynthesizer()
    res = await s.synthesize_async("")
    assert res.audio_pcm16_mono == b""
    assert res.duration == timedelta(0)


# ── keyword wake-word detector (pub-sub, concurrency) ───────────────────────────


async def test_keyword_wake_word_fires_only_when_listening_and_matching():
    events = []

    async def handler(e: WakeWordEvent) -> None:
        events.append(e)

    det = KeywordWakeWordDetector(keyword="hey b")
    assert det.backend_id == "keyword"
    sub = det.subscribe(handler)

    # Not listening yet -> no fire.
    assert await det.feed_async("hey b are you there") is False
    assert events == []

    await det.start_async()
    assert det.is_listening is True

    # Listening + no keyword -> no fire.
    assert await det.feed_async("good morning") is False
    # Listening + keyword (case-insensitive) -> fire.
    assert await det.feed_async("HEY B what's up") is True
    assert len(events) == 1
    assert events[0].keyword == "hey b"
    assert events[0].confidence == 1.0

    await det.stop_async()
    assert await det.feed_async("hey b") is False
    assert len(events) == 1

    sub.dispose()
    await det.dispose_async()


async def test_keyword_wake_word_handler_can_unsubscribe_without_deadlock():
    # A handler that disposes the detector mid-dispatch must not deadlock: the
    # subscriber list is snapshotted and the lock released before invocation.
    seen = []

    det = KeywordWakeWordDetector(keyword="stop")

    async def handler(e: WakeWordEvent) -> None:
        seen.append(e.keyword)
        await det.dispose_async()  # touches the lock the dispatch just released

    det.subscribe(handler)
    await det.start_async()
    assert await det.feed_async("please stop now") is True
    assert seen == ["stop"]


def test_keyword_wake_word_rejects_blank_keyword():
    with pytest.raises(ValueError):
        KeywordWakeWordDetector(keyword="   ")


# ── echo cancellers ─────────────────────────────────────────────────────────────


def test_null_echo_canceller_passthrough():
    ec = NullEchoCanceller.instance()
    assert ec.backend_id == "null"
    near = _pcm16(10, 20, 30)
    far = _pcm16(1, 2, 3)
    dst = bytearray(len(near))
    n = ec.cancel(near, far, 16_000, dst)
    assert n == len(near)
    assert bytes(dst) == near
    ec.reset()


def test_nlms_echo_canceller_removes_correlated_echo():
    ec = NlmsEchoCanceller(filter_length=16, step_size=0.5)
    assert ec.backend_id == "nlms"
    # near == far (pure echo). After adapting over a repeated signal the residual
    # error should shrink toward zero — later samples much smaller than input.
    import math

    samples = [int(8000 * math.sin(i * 0.3)) for i in range(400)]
    near = _pcm16(*samples)
    far = _pcm16(*samples)
    dst = bytearray(len(near))
    ec.cancel(near, far, 16_000, dst)
    out = _shorts(dst)
    # Energy of the last quarter should be well below the input energy.
    tail = out[-100:]
    tail_energy = sum(s * s for s in tail) / len(tail)
    in_energy = sum(s * s for s in samples[-100:]) / 100
    assert tail_energy < in_energy * 0.25


def test_nlms_length_mismatch_raises():
    ec = NlmsEchoCanceller()
    with pytest.raises(ValueError):
        ec.cancel(_pcm16(1, 2), _pcm16(1), 16_000, bytearray(4))


def test_nlms_reset_clears_state():
    ec = NlmsEchoCanceller(filter_length=8)
    near = _pcm16(*([5000] * 50))
    ec.cancel(near, near, 16_000, bytearray(len(near)))
    ec.reset()
    # After reset the first output equals the mic sample (weights all zero).
    dst = bytearray(2)
    ec.cancel(_pcm16(1234), _pcm16(1234), 16_000, dst)
    assert _shorts(dst)[0] == 1234  # error = mic - 0 = mic, clamped in range


def test_webrtc_echo_falls_back_to_nlms_without_runner():
    ec = WebRtcEchoCanceller()
    assert ec.backend_id == "webrtc-aec3 (fallback)"
    near = _pcm16(100, 200)
    dst = bytearray(len(near))
    ec.cancel(near, _pcm16(0, 0), 16_000, dst)  # far=0 -> output ~= near initially
    assert _shorts(dst) == (100, 200)


def test_webrtc_echo_uses_runner_when_present():
    class ZeroRunner(IEchoCancellerModelRunner):
        def process(self, near_end, far_end, sample_rate_hz, destination):
            for i in range(len(near_end)):
                destination[i] = 0
            return len(near_end)

        def reset(self):
            pass

    ec = WebRtcEchoCanceller(ZeroRunner())
    assert ec.backend_id == "webrtc-aec3"
    dst = bytearray(4)
    ec.cancel(_pcm16(9, 9), _pcm16(1, 1), 16_000, dst)
    assert bytes(dst) == b"\x00\x00\x00\x00"


# ── noise reducers ──────────────────────────────────────────────────────────────


def test_null_noise_reducer_passthrough():
    nr = NullNoiseReducer.instance()
    assert nr.backend_id == "null"
    assert nr.is_available is True
    src = _pcm16(1, 2, 3)
    dst = bytearray(len(src))
    assert nr.reduce(src, 16_000, dst) == len(src)
    assert bytes(dst) == src


def test_spectral_subtraction_attenuates_below_floor():
    nr = SpectralSubtractionNoiseReducer(floor_estimate=0.01, attenuation=0.25)
    assert nr.backend_id == "passthrough"
    floor = int(0.01 * 32767)  # 327
    quiet = 100  # below floor -> attenuated
    loud = 20000  # above floor -> passthrough
    src = _pcm16(quiet, loud)
    dst = bytearray(len(src))
    nr.reduce(src, 16_000, dst)
    out = _shorts(dst)
    assert out[0] == int(quiet * 0.25)
    assert out[1] == loud


def test_krisp_and_deepfilternet_fallback_ids():
    assert KrispNoiseReducer().backend_id == "krisp (fallback)"
    assert DeepFilterNetNoiseReducer().backend_id == "deepfilternet (fallback)"


def test_noise_reducer_uses_runner_when_present():
    class GainRunner(INoiseReducerModelRunner):
        def process(self, audio_pcm16_mono, sample_rate_hz, destination):
            destination[: len(audio_pcm16_mono)] = audio_pcm16_mono
            return len(audio_pcm16_mono)

    nr = KrispNoiseReducer(GainRunner())
    assert nr.backend_id == "krisp"
    src = _pcm16(7, 8)
    dst = bytearray(len(src))
    nr.reduce(src, 16_000, dst)
    assert bytes(dst) == src


# ── voice activity detectors ────────────────────────────────────────────────────


def test_null_vad_always_speech():
    vad = NullVoiceActivityDetector.instance()
    assert vad.backend_id == "null"
    assert vad.speech_threshold == 0.5
    res = vad.classify(_pcm16(0, 0), 16_000, timedelta(seconds=1))
    assert res.is_speech is True
    assert res.speech_probability == 1.0
    assert res.offset == timedelta(seconds=1)


def test_energy_vad_detects_loud_voiced_frame_and_hangover():
    vad = EnergyVoiceActivityDetector(speech_threshold=0.55, energy_threshold=0.05, hangover_frames=3)
    assert vad.backend_id == "energy"
    # Alternating loud samples -> high RMS + moderate ZCR -> speech.
    loud = _pcm16(*([20000, -20000] * 80))
    r = vad.classify(loud, 16_000, timedelta(0))
    assert r.is_speech is True
    # A subsequent silent frame is still speech due to hangover.
    silence = _pcm16(*([0] * 160))
    r2 = vad.classify(silence, 16_000, timedelta(milliseconds=10))
    assert r2.is_speech is True
    assert r2.speech_probability >= vad.speech_threshold


def test_energy_vad_silence_after_hangover_is_not_speech():
    vad = EnergyVoiceActivityDetector(energy_threshold=0.05, hangover_frames=1)
    loud = _pcm16(*([20000, -20000] * 80))
    vad.classify(loud, 16_000, timedelta(0))  # arms hangover=1
    silence = _pcm16(*([0] * 160))
    vad.classify(silence, 16_000, timedelta(0))  # consumes hangover
    r = vad.classify(silence, 16_000, timedelta(0))  # now silence
    assert r.is_speech is False


def test_energy_vad_tiny_frame_is_silence():
    vad = EnergyVoiceActivityDetector()
    r = vad.classify(b"\x01", 16_000, timedelta(0))
    assert r.is_speech is False
    assert r.speech_probability == 0.0


def test_silero_vad_falls_back_without_runner():
    vad = SileroVoiceActivityDetector()
    assert vad.backend_id == "silero (fallback)"
    loud = _pcm16(*([20000, -20000] * 80))
    assert vad.classify(loud, 16_000, timedelta(0)).is_speech is True


def test_silero_vad_uses_runner_score():
    class FixedRunner(IVadModelRunner):
        def __init__(self, p):
            self._p = p

        def score_frame(self, audio_pcm16_mono, sample_rate_hz):
            return self._p

    vad = SileroVoiceActivityDetector(FixedRunner(0.9), speech_threshold=0.5)
    assert vad.backend_id == "silero"
    r = vad.classify(_pcm16(0), 16_000, timedelta(0))
    assert r.is_speech is True
    assert r.speech_probability == pytest.approx(0.9)


# ── end-of-turn detectors ───────────────────────────────────────────────────────


def test_null_eot_always_complete():
    d = NullEndOfTurnDetector.instance()
    assert d.backend_id == "null"
    r = d.predict("anything", timedelta(0))
    assert r == EndOfTurnResult(is_complete=True, confidence=1.0, wait_more_ms=0)


def test_rule_eot_terminal_punctuation_after_min_silence():
    d = RuleBasedEndOfTurnDetector()
    r = d.predict("I am done.", timedelta(milliseconds=500))
    assert r.is_complete is True
    assert r.confidence == pytest.approx(0.9)


def test_rule_eot_hanging_word_extends_wait():
    d = RuleBasedEndOfTurnDetector()
    r = d.predict("I was going to and", timedelta(milliseconds=100))
    assert r.is_complete is False
    assert r.wait_more_ms > 0


def test_rule_eot_max_silence_forces_complete():
    d = RuleBasedEndOfTurnDetector()
    r = d.predict("um", timedelta(milliseconds=3000))
    assert r.is_complete is True
    assert r.confidence == pytest.approx(0.7)


def test_rule_eot_empty_text_waits():
    d = RuleBasedEndOfTurnDetector()
    r = d.predict("", timedelta(milliseconds=0))
    assert r.is_complete is False
    assert r.wait_more_ms >= 150


def test_smart_turn_falls_back_without_runner():
    d = SmartTurnDetector()
    assert d.backend_id == "smart-turn (fallback)"
    r = d.predict("done.", timedelta(milliseconds=500))
    assert r.is_complete is True


def test_smart_turn_uses_runner_score():
    class Runner(ITurnModelRunner):
        def __init__(self, p):
            self._p = p

        def score_completion(self, partial_transcript, trailing_silence):
            return self._p

    d = SmartTurnDetector(Runner(0.8), threshold=0.5)
    assert d.backend_id == "smart-turn-v2"
    r = d.predict("hi", timedelta(0))
    assert r.is_complete is True
    assert r.confidence == pytest.approx(0.8)

    d2 = SmartTurnDetector(Runner(0.2), threshold=0.5)
    r2 = d2.predict("hi", timedelta(0))
    assert r2.is_complete is False
    # (1 - 0.2) * 1000 = 800 ms
    assert r2.wait_more_ms == 800


# ── audio format converter ──────────────────────────────────────────────────────


def test_resample_identity_and_doubling():
    pcm = _pcm16(100, 200, 300, 400)
    assert resample_pcm16_linear(pcm, 8000, 8000) == pcm
    up = resample_pcm16_linear(pcm, 8000, 16000)
    assert len(up) == 8 * 2  # 4 samples -> 8 samples


def test_mu_law_roundtrip_monotonic():
    ramp = _pcm16(-30000, -10000, -1000, 100, 1000, 10000, 30000)
    rt = _shorts(decode_mu_law_to_pcm16(encode_pcm16_to_mu_law(ramp)))
    assert all(rt[i] <= rt[i + 1] for i in range(len(rt) - 1))


def test_a_law_roundtrip_monotonic():
    ramp = _pcm16(-30000, -10000, -1000, 100, 1000, 10000, 30000)
    rt = _shorts(decode_a_law_to_pcm16(encode_pcm16_to_a_law(ramp)))
    assert all(rt[i] <= rt[i + 1] for i in range(len(rt) - 1))


def test_mu_law_known_codewords():
    # ITU G.711: 0xFF (~0x00 after invert) decodes to 0; 0x00 to a large magnitude.
    assert _shorts(decode_mu_law_to_pcm16(bytes([0xFF]))) == (0,)
    assert _shorts(decode_mu_law_to_pcm16(bytes([0x00])))[0] < -30000


def test_convert_full_path_pcm_to_mulaw_shape():
    pcm = _pcm16(*([1000] * 8))  # 8 samples @ 8k
    out = AudioFormatConverter.convert(pcm, AudioCodec.PCM16, 8000, AudioCodec.MU_LAW, 8000)
    assert len(out) == 8  # 1 byte per μ-law sample


def test_convert_rejects_bad_sample_rate():
    with pytest.raises(ValueError):
        AudioFormatConverter.convert(b"\x00\x00", AudioCodec.PCM16, 0, AudioCodec.PCM16, 8000)


def test_convert_mulaw_to_pcm_resampled():
    # μ-law 8k -> PCM16 16k: decode then upsample. 4 μ-law bytes -> 4 samples ->
    # 8 samples PCM -> 16 bytes.
    mulaw = bytes([0x7F, 0x80, 0x00, 0xFF])
    out = AudioFormatConverter.convert(mulaw, AudioCodec.MU_LAW, 8000, AudioCodec.PCM16, 16000)
    assert len(out) == 8 * 2
