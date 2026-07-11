# carrier_fallback.py
#
# Port of the CarrierFallback class from CircleAI.Telephony
# ServiceCollectionExtensions.cs (C# — the EXACT spec).
#
# (3.3.0) The DI extension methods (AddCircleAiTelephony / AddCarrierFallback)
# are NOT ported — the Python tree has no DI container; wire the null defaults
# and the provisioner via their constructors instead. But the multi-carrier
# failover logic that those extensions register (``CarrierFallback``) is real
# behaviour, so it lives here as a standalone, constructor-wired class.
#
# C# ``internal sealed class`` -> a public module-level class (Python has no
# assembly-internal access modifier). C# LINQ Any/FirstOrDefault -> any()/next().

from __future__ import annotations

from typing import Iterable, List, Optional

from .contracts import ICallSession, ITelephonyCarrier, OutboundDialOptions
from .null_implementations import NullTelephonyCarrier
from .primitives import ProvisionedNumber


class CarrierFallback(ITelephonyCarrier):
    """(3.3.0) Multi-carrier failover — picks the first configured carrier."""

    def __init__(self, carriers: Optional[Iterable[ITelephonyCarrier]]) -> None:
        self._carriers: List[ITelephonyCarrier] = list(carriers) if carriers is not None else []

    @property
    def carrier_id(self) -> str:
        return f"fallback({len(self._carriers)})"

    @property
    def is_configured(self) -> bool:
        return any(c.is_configured for c in self._carriers)

    def _pick(self) -> ITelephonyCarrier:
        for c in self._carriers:
            if c.is_configured:
                return c
        return NullTelephonyCarrier.Instance

    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        return await self._pick().provision_number_async(country_code, area_code, ct=ct)

    async def configure_inbound_webhook_async(
        self,
        phone_number: str,
        inbound_webhook: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        return await self._pick().configure_inbound_webhook_async(
            phone_number, inbound_webhook, ct=ct
        )

    async def dial_async(
        self,
        from_number: str,
        to_number: str,
        stream_url: str,
        options: Optional[OutboundDialOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> ICallSession:
        return await self._pick().dial_async(from_number, to_number, stream_url, options, ct=ct)

    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        return await self._pick().list_numbers_async(ct=ct)
