"""circle_ai.markets — port of the CircleAI.Markets assembly.

(2.8.0 contracts / 3.3.0 in-memory impl) Markets domain: instruments, quotes,
order requests/results, the market-data-feed / instrument-catalog / order-router
contracts (OrderSide / OrderType enums), a concurrent in-memory feed with
subscribe/broadcast quote pushes, a searchable catalog, a rules-based order
router, and fail-closed null defaults. C# is the exact spec.

Public surface:

  * OrderSide / OrderType — enums (IntEnum, C# ordinals).
  * Instrument / Quote / OrderRequest / OrderResult — domain records.
  * IMarketDataFeed / IInstrumentCatalog / IOrderRouter — backend contracts.
  * IDisposable — quote-subscription handle.
  * InMemoryMarketDataFeed / InMemoryInstrumentCatalog / InMemoryOrderRouter.
  * NullMarketDataFeed / NullInstrumentCatalog / NullOrderRouter — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    IDisposable,
    IInstrumentCatalog,
    IMarketDataFeed,
    IOrderRouter,
    Instrument,
    OrderRequest,
    OrderResult,
    OrderSide,
    OrderType,
    Quote,
)
from .in_memory_markets import (
    InMemoryInstrumentCatalog,
    InMemoryMarketDataFeed,
    InMemoryOrderRouter,
)
from .null_implementations import (
    NullInstrumentCatalog,
    NullMarketDataFeed,
    NullOrderRouter,
)

__all__ = [
    "OrderSide",
    "OrderType",
    "Instrument",
    "Quote",
    "OrderRequest",
    "OrderResult",
    "IMarketDataFeed",
    "IInstrumentCatalog",
    "IOrderRouter",
    "IDisposable",
    "InMemoryMarketDataFeed",
    "InMemoryInstrumentCatalog",
    "InMemoryOrderRouter",
    "NullMarketDataFeed",
    "NullInstrumentCatalog",
    "NullOrderRouter",
]
