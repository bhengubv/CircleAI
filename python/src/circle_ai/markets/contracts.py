# contracts.py
#
# Port of CircleAI.Markets Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Markets contracts. Real in-memory backends 3.3.0.
#
# C# ValueTask/ValueTask<T> maps to async def -> None/T. C# records map to frozen
# slotted dataclasses. C# decimal (exact money/price) maps to decimal.Decimal,
# DateTimeOffset -> datetime. OrderSide / OrderType are IntEnums with the C#
# ordinals. The quote-subscription handle is an IDisposable (mirrors C#
# IDisposable), and SubscribeQuotes takes an async handler
# (Func<Quote, ValueTask>).

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from enum import IntEnum
from typing import Awaitable, Callable, List, Optional


class OrderSide(IntEnum):
    """Mirrors ``CircleAI.Markets.OrderSide`` (Buy=0, Sell=1)."""

    Buy = 0
    Sell = 1


class OrderType(IntEnum):
    """Mirrors ``CircleAI.Markets.OrderType`` (Market=0, Limit=1)."""

    Market = 0
    Limit = 1


@dataclass(frozen=True, slots=True)
class Instrument:
    """Mirrors ``CircleAI.Markets.Instrument`` — ``record(string Symbol,
    string Exchange, string Currency, string AssetClass)``.
    """

    symbol: str
    exchange: str
    currency: str
    asset_class: str


@dataclass(frozen=True, slots=True)
class Quote:
    """Mirrors ``CircleAI.Markets.Quote`` — ``record(string Symbol, decimal Bid,
    decimal Ask, decimal Last, DateTimeOffset AtUtc)``.
    """

    symbol: str
    bid: Decimal
    ask: Decimal
    last: Decimal
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class OrderRequest:
    """Mirrors ``CircleAI.Markets.OrderRequest`` — ``record(string Symbol,
    OrderSide Side, OrderType Type, decimal Quantity, decimal? LimitPrice)``.
    """

    symbol: str
    side: OrderSide
    type: OrderType
    quantity: Decimal
    limit_price: Optional[Decimal]


@dataclass(frozen=True, slots=True)
class OrderResult:
    """Mirrors ``CircleAI.Markets.OrderResult`` — ``record(string OrderId,
    bool Accepted, string? FailureReason)``.
    """

    order_id: str
    accepted: bool
    failure_reason: Optional[str]


#: An async quote handler, mirroring C# ``Func<Quote, ValueTask>``.
QuoteHandler = Callable[[Quote], Awaitable[None]]


class IDisposable(ABC):
    """A resource that can be released. Mirrors C# ``IDisposable`` — the handle
    returned by :meth:`IMarketDataFeed.subscribe_quotes`. Usable as a context
    manager.
    """

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IMarketDataFeed(ABC):
    """(2.8.0) Market-data feed contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_quote_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Quote]:
        ...

    @abstractmethod
    def subscribe_quotes(self, symbol: str, handler: QuoteHandler) -> IDisposable:
        ...


class IInstrumentCatalog(ABC):
    """(2.8.0) Instrument catalog contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Instrument]:
        ...

    @abstractmethod
    async def search_async(
        self, query: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Instrument]:
        ...


class IOrderRouter(ABC):
    """(2.8.0) Order router contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def submit_async(
        self, req: OrderRequest, ct: Optional[object] = None
    ) -> OrderResult:
        ...
