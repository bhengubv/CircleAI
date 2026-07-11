# disposable.py
#
# (3.3.0) Local IDisposable ABC for the telephony surface — the Python analogue
# of System.IDisposable, used by subscription handles (ISpeechSubscription, the
# inbound-dispatcher subscribe handle, etc.). Mirrors the pattern already used
# in circle_ai.aethernet. Supports the ``with`` statement so callers can scope a
# subscription the way C# scopes a ``using``.

from __future__ import annotations

from abc import ABC, abstractmethod


class IDisposable(ABC):
    """(3.3.0) C# ``System.IDisposable`` — deterministic cleanup handle."""

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class _ActionDisposable(IDisposable):
    """(3.3.0) IDisposable that runs a supplied zero-arg callable once on dispose.

    Mirrors the private ``SubHandle`` / lambda-backed disposables the C# uses for
    subscription teardown. Idempotent — dispose runs the action at most once.
    """

    __slots__ = ("_action", "_disposed")

    def __init__(self, action) -> None:
        self._action = action
        self._disposed = False

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        if self._action is not None:
            self._action()


class _NoopDisposable(IDisposable):
    """(3.3.0) Disposable that does nothing. Mirrors the C# ``NoopDisposable``."""

    Instance: "_NoopDisposable"

    def dispose(self) -> None:
        pass


_NoopDisposable.Instance = _NoopDisposable()
