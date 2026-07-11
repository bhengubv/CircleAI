# phone_number_provisioner.py
#
# Port of CircleAI.Telephony PhoneNumberProvisioner.cs (C# — the EXACT spec).
#
# (3.3.0) Orchestrates the "buy + configure + persist" loop across any carrier
# that implements ITelephonyCarrier. Single call: pick a country, supply your
# inbound webhook, get back a ProvisionedNumber that's ready to take calls.
#
# C# ILogger -> the stdlib logging.Logger (NullLogger default -> a module logger
# that no-ops unless the host configures handlers). C# Uri -> str; the
# IsAbsoluteUri guard maps to a scheme+netloc check via urllib. Case-insensitive
# ordinal number dictionaries are modelled with casefold() keys so the
# de-dup / merge order matches C# StringComparer.OrdinalIgnoreCase.

from __future__ import annotations

import logging
import threading
from abc import ABC, abstractmethod
from typing import Dict, List, Optional
from urllib.parse import urlsplit

from .contracts import ITelephonyCarrier
from .primitives import ProvisionedNumber

_logger = logging.getLogger("CircleAI.Telephony.PhoneNumberProvisioner")


def _is_absolute_uri(uri: str) -> bool:
    parts = urlsplit(uri)
    return bool(parts.scheme) and bool(parts.netloc)


class IProvisionedNumberStore(ABC):
    """(3.3.0) Persistence contract for assigned numbers. Default in-memory
    implementation is fine for dev; production hosts should plug in a
    database-backed store."""

    @abstractmethod
    async def save_async(self, number: ProvisionedNumber, *, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def list_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        ...

    @abstractmethod
    async def find_async(
        self, phone_number: str, *, ct: Optional[object] = None
    ) -> Optional[ProvisionedNumber]:
        ...

    @abstractmethod
    async def remove_async(self, phone_number: str, *, ct: Optional[object] = None) -> None:
        ...


class InMemoryProvisionedNumberStore(IProvisionedNumberStore):
    """(3.3.0) Default in-memory store. Thread-safe."""

    def __init__(self) -> None:
        # keyed by casefold(phone_number) to mirror OrdinalIgnoreCase.
        self._by_number: Dict[str, ProvisionedNumber] = {}
        self._gate = threading.Lock()

    async def save_async(self, number: ProvisionedNumber, *, ct: Optional[object] = None) -> None:
        if number is None:
            raise ValueError("number must not be None")
        with self._gate:
            self._by_number[number.phone_number.casefold()] = number

    async def list_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        with self._gate:
            return list(self._by_number.values())

    async def find_async(
        self, phone_number: str, *, ct: Optional[object] = None
    ) -> Optional[ProvisionedNumber]:
        with self._gate:
            return self._by_number.get(phone_number.casefold())

    async def remove_async(self, phone_number: str, *, ct: Optional[object] = None) -> None:
        with self._gate:
            self._by_number.pop(phone_number.casefold(), None)


class PhoneNumberProvisioner:
    """(3.3.0) Service that buys + configures + persists phone numbers from any
    carrier behind :class:`ITelephonyCarrier`."""

    def __init__(
        self,
        carrier: ITelephonyCarrier,
        store: Optional[IProvisionedNumberStore] = None,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        if carrier is None:
            raise ValueError("carrier must not be None")
        self._carrier = carrier
        self._store = store if store is not None else InMemoryProvisionedNumberStore()
        self._logger = logger if logger is not None else _logger

    async def provision_async(
        self,
        country_code: str,
        inbound_webhook: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        """(3.3.0) Buy a number, wire its inbound webhook, persist it, return the
        metadata.

        ``country_code``: ISO country code (e.g. "US", "ZA", "NG").
        ``inbound_webhook``: HTTPS URL the carrier will hit when the number rings.
        ``area_code``: optional area code / prefix preference.
        """
        if not country_code or country_code.isspace():
            raise ValueError("countryCode is required")
        if inbound_webhook is None:
            raise ValueError("inbound_webhook must not be None")
        if not _is_absolute_uri(inbound_webhook):
            raise ValueError("inboundWebhook must be an absolute URI")

        self._logger.info(
            "Provisioning number on %s for %s/%s",
            self._carrier.carrier_id,
            country_code,
            area_code if area_code is not None else "(any)",
        )

        provisioned = await self._carrier.provision_number_async(country_code, area_code, ct=ct)

        try:
            await self._carrier.configure_inbound_webhook_async(
                provisioned.phone_number, inbound_webhook, ct=ct
            )
        except Exception:
            self._logger.error(
                "Webhook configuration failed for %s on %s",
                provisioned.phone_number,
                self._carrier.carrier_id,
                exc_info=True,
            )
            raise

        await self._store.save_async(provisioned, ct=ct)
        return provisioned

    async def list_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        """(3.3.0) The provisioned numbers we know about, locally + via the carrier."""
        stored = await self._store.list_async(ct=ct)
        # Merge with carrier authoritative list — store may be stale.
        carrier_numbers = await self._carrier.list_numbers_async(ct=ct)
        merged: Dict[str, ProvisionedNumber] = {}
        for n in stored:
            merged[n.phone_number.casefold()] = n
        for n in carrier_numbers:
            merged[n.phone_number.casefold()] = n
        return list(merged.values())
