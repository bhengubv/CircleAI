# null_implementations.py
#
# Port of CircleAI.Markets NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed markets defaults. The C# `static readonly Instance`
# singleton maps to a module-level singleton bound after the class body.
# Guid.Empty.ToString() renders the all-zero GUID in the default ("D") dashed
# format. The null feed's SubscribeQuotes returns a shared no-op disposable.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    IDisposable,
    IInstrumentCatalog,
    IMarketDataFeed,
    IOrderRouter,
    Instrument,
    OrderRequest,
    OrderResult,
    Quote,
    QuoteHandler,
)

_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"


class _EmptyDisposable(IDisposable):
    Instance: "_EmptyDisposable"

    def dispose(self) -> None:
        pass


_EmptyDisposable.Instance = _EmptyDisposable()


class NullMarketDataFeed(IMarketDataFeed):
    Instance: "NullMarketDataFeed"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_quote_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Quote]:
        return None

    def subscribe_quotes(self, symbol: str, h: QuoteHandler) -> IDisposable:
        return _EmptyDisposable.Instance


class NullInstrumentCatalog(IInstrumentCatalog):
    Instance: "NullInstrumentCatalog"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Instrument]:
        return None

    async def search_async(
        self, q: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Instrument]:
        return []


class NullOrderRouter(IOrderRouter):
    Instance: "NullOrderRouter"

    @property
    def backend_id(self) -> str:
        return "null"

    async def submit_async(
        self, req: OrderRequest, ct: Optional[object] = None
    ) -> OrderResult:
        return OrderResult(_GUID_EMPTY, False, "NullOrderRouter — fail-closed.")


NullMarketDataFeed.Instance = NullMarketDataFeed()
NullInstrumentCatalog.Instance = NullInstrumentCatalog()
NullOrderRouter.Instance = NullOrderRouter()
