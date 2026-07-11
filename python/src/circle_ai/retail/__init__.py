"""circle_ai.retail — port of the CircleAI.Retail assembly.

(3.3.0) Real domain types + in-memory store for the Retail vertical: products,
stock levels, sales, daily summary — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Product / StockLevel / Sale — domain records.
  * IRetailBoard        — product / stock / sales board.
  * InMemoryRetailBoard — thread-safe in-memory board.
  * RetailDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``RetailCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .retail_domain_context import RetailDomainContext
from .retail_primitives import (
    IRetailBoard,
    InMemoryRetailBoard,
    Product,
    Sale,
    StockLevel,
)

__all__ = [
    "Product",
    "StockLevel",
    "Sale",
    "IRetailBoard",
    "InMemoryRetailBoard",
    "RetailDomainContext",
]
