"""circle_ai.commerce_accounting — port of the CircleAI.Commerce.Accounting assembly.

(3.3.0) Real domain types + in-memory board for the Commerce.Accounting
vertical: double-entry ledger, tax rates, per-period sums, net profit — plus the
static domain context (system-prompt snippet, compliance flags, suggested
tools). C# is the exact spec.

Public surface:

  * AccountingEntry / TaxRate / Period — domain records.
  * IAccountingBoard        — post / tax / balance / sum / net-profit board.
  * InMemoryAccountingBoard — thread-safe in-memory board.
  * CommerceAccountingDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``CommerceAccountingCompanionAdapter`` decorates ``CircleAI.
Companion.ICompanionSession``, which is not part of the ported Python companion
surface, so it is intentionally not ported here.
"""
from __future__ import annotations

from .accounting_primitives import (
    AccountingEntry,
    IAccountingBoard,
    InMemoryAccountingBoard,
    Period,
    TaxRate,
)
from .commerce_accounting_domain_context import CommerceAccountingDomainContext

__all__ = [
    "AccountingEntry",
    "TaxRate",
    "Period",
    "IAccountingBoard",
    "InMemoryAccountingBoard",
    "CommerceAccountingDomainContext",
]
