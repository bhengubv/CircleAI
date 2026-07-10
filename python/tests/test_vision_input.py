"""test_vision_input.py — VisionInput container."""
from __future__ import annotations

import pytest

from circle_ai.inference import VisionInput


def test_vision_input_holds_bytes_and_mime():
    v = VisionInput(image_bytes=b"\x89PNG", mime_type="image/png")
    assert v.image_bytes == b"\x89PNG"
    assert v.mime_type == "image/png"


def test_vision_input_mime_optional():
    v = VisionInput(image_bytes=b"data")
    assert v.mime_type is None


def test_vision_input_is_frozen():
    v = VisionInput(image_bytes=b"data")
    with pytest.raises(Exception):
        v.image_bytes = b"other"  # frozen dataclass


def test_vision_input_requires_bytes():
    with pytest.raises(ValueError):
        VisionInput(image_bytes=None)  # type: ignore[arg-type]
