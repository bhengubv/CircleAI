"""circle_ai.commerce — port of the CircleAI.Commerce assembly.

(3.3.0) Real domain types + in-memory board for the Commerce vertical:
customers, orders, line items, lifetime value — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * CommerceCustomer / CommerceOrder / CommerceLineItem — domain records.
  * ICommerceBoard        — customer / order / line-item board.
  * InMemoryCommerceBoard — thread-safe in-memory board.
  * CommerceDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``CommerceCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .commerce_domain_context import CommerceDomainContext
from .commerce_primitives import (
    CommerceCustomer,
    CommerceLineItem,
    CommerceOrder,
    ICommerceBoard,
    InMemoryCommerceBoard,
)

__all__ = [
    "CommerceCustomer",
    "CommerceOrder",
    "CommerceLineItem",
    "ICommerceBoard",
    "InMemoryCommerceBoard",
    "CommerceDomainContext",
]
