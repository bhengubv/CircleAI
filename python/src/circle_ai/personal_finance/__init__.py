"""circle_ai.personal_finance — port of the CircleAI.Personal.Finance assembly.

(3.3.0) Real domain types + in-memory board for personal finance: accounts,
transactions, budgets, monthly summary — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Account / FinanceTransaction / BudgetLine / MonthSummary — domain records.
  * IPersonalFinanceBoard        — account / transaction / budget board.
  * InMemoryPersonalFinanceBoard — thread-safe in-memory board.
  * PersonalFinanceDomainContext — static system-prompt + compliance/tool metadata.

Note: ``Account`` here is the personal-finance account record and is distinct
from ``circle_ai.banking.Account``; import from the subpackage to disambiguate.
The C# ``PersonalFinanceCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession`` (not part of the ported Python companion surface) and is
intentionally not ported.
"""
from __future__ import annotations

from .personal_finance_domain_context import PersonalFinanceDomainContext
from .personal_finance_primitives import (
    Account,
    BudgetLine,
    FinanceTransaction,
    IPersonalFinanceBoard,
    InMemoryPersonalFinanceBoard,
    MonthSummary,
)

__all__ = [
    "Account",
    "FinanceTransaction",
    "BudgetLine",
    "MonthSummary",
    "IPersonalFinanceBoard",
    "InMemoryPersonalFinanceBoard",
    "PersonalFinanceDomainContext",
]
