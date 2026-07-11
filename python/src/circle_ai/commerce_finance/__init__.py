"""circle_ai.commerce_finance — port of the CircleAI.Commerce.Finance assembly.

(3.3.0) Real domain types + in-memory board for the Commerce.Finance vertical:
invoices, invoice lines, payments, overdue tracking, outstanding balances —
plus the static domain context (system-prompt snippet, compliance flags,
suggested tools). C# is the exact spec.

Public surface:

  * InvoiceLine / Invoice / FinancePayment — domain records.
  * IInvoiceBoard        — issue / payment / overdue / outstanding board.
  * InMemoryInvoiceBoard — thread-safe in-memory board.
  * CommerceFinanceDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``CommerceFinanceCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .commerce_finance_domain_context import CommerceFinanceDomainContext
from .finance_primitives import (
    FinancePayment,
    IInvoiceBoard,
    InMemoryInvoiceBoard,
    Invoice,
    InvoiceLine,
)

__all__ = [
    "InvoiceLine",
    "Invoice",
    "FinancePayment",
    "IInvoiceBoard",
    "InMemoryInvoiceBoard",
    "CommerceFinanceDomainContext",
]
