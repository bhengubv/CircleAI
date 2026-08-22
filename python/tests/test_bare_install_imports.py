"""test_bare_install_imports.py

pyproject declares ``dependencies = []`` and documents "bare install is pure
stdlib; add what you need". This test is what makes that a promise rather than a
comment.

It exists because it was NOT true: ``security/uhid_key_ring.py`` imported
``cryptography`` at module scope, ``circle_ai/__init__.py`` imports that module,
and so a bare install could not import the package at all — every test in the
port failed at COLLECTION, with a ModuleNotFoundError naming a file most callers
have never heard of. Nothing said "you are missing an optional dependency".
"""
from __future__ import annotations

import builtins
import importlib
import sys

import pytest


@pytest.fixture
def without_cryptography(monkeypatch: pytest.MonkeyPatch):
    """Make ``import cryptography`` fail, as it does on a bare install."""
    real_import = builtins.__import__

    def fake_import(name: str, *args, **kwargs):
        if name == "cryptography" or name.startswith("cryptography."):
            raise ImportError(f"No module named {name!r}")
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", fake_import)

    # Drop anything already imported so the guarded import path actually runs.
    for mod in [m for m in sys.modules if m.startswith("cryptography")]:
        monkeypatch.delitem(sys.modules, mod, raising=False)
    for mod in [m for m in sys.modules if m.startswith("circle_ai")]:
        monkeypatch.delitem(sys.modules, mod, raising=False)
    yield


def test_package_imports_without_cryptography(without_cryptography) -> None:
    """The whole package must import on bare stdlib.

    This is the regression that mattered: the failure was not in a corner of the
    security module, it was the entire port refusing to load.
    """
    importlib.import_module("circle_ai")


def test_key_ring_module_imports_without_cryptography(without_cryptography) -> None:
    module = importlib.import_module("circle_ai.security.uhid_key_ring")
    assert module.UhidKeyRing is not None


def test_using_the_key_ring_names_the_missing_package(without_cryptography) -> None:
    """Failure is deferred to USE, and says what to install."""
    module = importlib.import_module("circle_ai.security.uhid_key_ring")
    with pytest.raises(ImportError) as excinfo:
        module.UhidKeyRing("uhid-test")

    message = str(excinfo.value)
    assert "cryptography" in message, "the error must name the package"
    assert "security" in message, "the error must name the extra that installs it"


def test_key_ring_works_when_cryptography_is_present() -> None:
    """And with the extra installed it must still do real ECDSA P-256."""
    cryptography = pytest.importorskip(
        "cryptography", reason="install the 'security' extra to exercise the real path"
    )
    assert cryptography is not None

    from circle_ai.security.uhid_key_ring import UhidKeyRing

    with UhidKeyRing("uhid-test") as ring:
        signature = ring.sign(b"payload")
        assert ring.verify(b"payload", signature) is True
        assert ring.verify(b"tampered", signature) is False
