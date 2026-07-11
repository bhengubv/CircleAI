# null_implementations.py
#
# Port of CircleAI.Telephony NullImplementations.cs (C# — the EXACT spec).
#
# (3.3.0) No-op fallbacks for the telephony surface. Used when the host hasn't
# wired a real carrier — test runs, dry-runs, or "telephony not configured"
# composition lines.
#
# The C# ``static readonly Instance`` singletons map to module-level singletons
# created after each class body. C# InvalidOperationException -> RuntimeError.

from __future__ import annotations

from typing import Callable, List, Optional

from .contracts import (
    ICallSession,
    IInboundCallDispatcher,
    ITelephonyCarrier,
    OutboundDialOptions,
)
from .disposable import IDisposable, _NoopDisposable
from .primitives import ProvisionedNumber


class NullTelephonyCarrier(ITelephonyCarrier):
    """(3.3.0) Null carrier — fail-soft on every operation."""

    Instance: "NullTelephonyCarrier"

    @property
    def carrier_id(self) -> str:
        return "null"

    @property
    def is_configured(self) -> bool:
        return False

    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        raise RuntimeError(
            "Null carrier cannot provision phone numbers. Register a real "
            "ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo)."
        )

    async def configure_inbound_webhook_async(
        self,
        phone_number: str,
        inbound_webhook: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        return None

    async def dial_async(
        self,
        from_number: str,
        to_number: str,
        stream_url: str,
        options: Optional[OutboundDialOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> ICallSession:
        raise RuntimeError(
            "Null carrier cannot place outbound calls. Register a real ITelephonyCarrier."
        )

    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        return []


class NullInboundCallDispatcher(IInboundCallDispatcher):
    """(3.3.0) Null inbound dispatcher — never fires."""

    Instance: "NullInboundCallDispatcher"

    @property
    def carrier_id(self) -> str:
        return "null"

    def subscribe(self, handler: Callable[[ICallSession], object]) -> IDisposable:
        return _NoopDisposable.Instance


NullTelephonyCarrier.Instance = NullTelephonyCarrier()
NullInboundCallDispatcher.Instance = NullInboundCallDispatcher()
