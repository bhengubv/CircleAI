# auth_challenge.py
#
# Port of CircleAI.Aether.IAuthChallenge.cs (C# — the EXACT spec).
#
# Contract 5 — Auth Challenge.
#
# Bidirectional trust gate.
#   -> User auth enables the security layer at OS level.
#   <- Security layer demands re-auth when threat thresholds are crossed.
#
# Minimum: Biometric + DeviceAdmin for OS-level operations. Developers can raise
# the bar; they cannot lower it below the minimum.
#
# Ships:
#   AuthChallengeReason      — why a challenge is being issued
#   AuthMethod               — the method used/required (ordered by strength)
#   AuthChallengeResult      — the outcome record (+ success/failure factories)
#   IAuthChallenge           — the challenge contract
#   InMemoryAuthChallenge    — a working, deterministic implementation

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class AuthChallengeReason(IntEnum):
    """Why an auth challenge is being issued."""

    # The user is enabling or disabling the OS-level Aether service.
    OS_LEVEL_TOGGLE = 0

    # The AI Security Layer detected anomaly scores above the configured
    # threshold and requires the user to confirm their identity.
    THREAT_THRESHOLD_REACHED = 1

    # The operation being attempted requires elevated auth.
    PRIVILEGED_OPERATION = 2

    # Scheduled trust renewal — periodic re-validation.
    PERIODIC_REVALIDATION = 3

    # Explicitly triggered by the developer or admin.
    MANUAL_REQUEST = 4


class AuthMethod(IntEnum):
    """The authentication method used or required. Methods are ordered by
    strength; higher numeric values are stronger.
    """

    # Fingerprint, face, or iris recognition.
    BIOMETRIC = 1

    # Device administrator credential (PIN, password, pattern).
    DEVICE_ADMIN = 2

    # Biometric AND device admin — the minimum for any OS-level operation.
    BIOMETRIC_AND_DEVICE_ADMIN = 3

    # Developer-defined method layered on top of BiometricAndDeviceAdmin.
    CUSTOM = 4


@dataclass(frozen=True, slots=True)
class AuthChallengeResult:
    """The outcome of an auth challenge."""

    succeeded: bool
    method_used: AuthMethod
    failure_reason: Optional[str]
    completed_at: datetime

    @staticmethod
    def success(method: AuthMethod) -> "AuthChallengeResult":
        """Convenience: a successful result with no failure reason."""
        return AuthChallengeResult(True, method, None, _utc_now())

    @staticmethod
    def failure(method: AuthMethod, reason: str) -> "AuthChallengeResult":
        """Convenience: a failed result with an explanatory reason."""
        return AuthChallengeResult(False, method, reason, _utc_now())


class IAuthChallenge(ABC):
    """Issues and resolves authentication challenges for security-sensitive
    operations. Platform adapters (MAUI, server) implement this using native
    biometric and device admin APIs.
    """

    @abstractmethod
    async def challenge_async(
        self,
        reason: AuthChallengeReason,
        minimum_method: Optional[AuthMethod],
        prompt: str,
        ct: Optional[object] = None,
    ) -> AuthChallengeResult:
        """Presents an auth challenge to the user for the given reason. The
        platform adapter enforces the minimum method requirement.

        :param minimum_method: The weakest method acceptable. Defaults to
            :attr:`AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN` when None.
        """
        ...

    @abstractmethod
    async def request_os_toggle_async(
        self, enable: bool, ct: Optional[object] = None
    ) -> AuthChallengeResult:
        """Presents the OS-level toggle challenge. Always requires
        :attr:`AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN` at minimum.
        """
        ...


class InMemoryAuthChallenge(IAuthChallenge):
    """A working, deterministic :class:`IAuthChallenge`. Enforces the same
    minimum-method floor the platform adapters do — any request whose minimum is
    weaker than :attr:`AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN` is raised to that
    floor — and satisfies challenges with a configurable outcome.

    :param should_succeed: When True (default) every challenge succeeds with the
        effective method; when False every challenge fails with a fixed reason.
    :param satisfied_method: The method reported as used on success. Must be at
        least as strong as the effective minimum, else it is raised to it.
    """

    #: The floor no OS-level operation may drop below.
    MINIMUM_METHOD = AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN

    def __init__(
        self,
        should_succeed: bool = True,
        satisfied_method: AuthMethod = AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN,
    ) -> None:
        self._should_succeed = should_succeed
        self._satisfied_method = satisfied_method

    async def challenge_async(
        self,
        reason: AuthChallengeReason,
        minimum_method: Optional[AuthMethod],
        prompt: str,
        ct: Optional[object] = None,
    ) -> AuthChallengeResult:
        effective_min = self._effective_minimum(minimum_method)
        # The method actually used can never be weaker than the required floor.
        used = (
            self._satisfied_method
            if self._satisfied_method >= effective_min
            else effective_min
        )
        if self._should_succeed:
            return AuthChallengeResult.success(used)
        return AuthChallengeResult.failure(used, "User cancelled or auth failed.")

    async def request_os_toggle_async(
        self, enable: bool, ct: Optional[object] = None
    ) -> AuthChallengeResult:
        # OS toggle always demands the full floor.
        return await self.challenge_async(
            AuthChallengeReason.OS_LEVEL_TOGGLE,
            self.MINIMUM_METHOD,
            f"{'Enable' if enable else 'Disable'} the Aether service.",
            ct,
        )

    def _effective_minimum(self, requested: Optional[AuthMethod]) -> AuthMethod:
        # Null defaults to the floor; a weaker request is raised to the floor.
        if requested is None:
            return self.MINIMUM_METHOD
        return requested if requested >= self.MINIMUM_METHOD else self.MINIMUM_METHOD
