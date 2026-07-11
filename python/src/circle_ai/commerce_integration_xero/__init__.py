"""circle_ai.commerce_integration_xero — port of the
CircleAI.Commerce.Integration.Xero assembly.

(3.3.0) Xero integration primitives — token storage, tenant tracking (dedup by
tenant id), webhook recorder — plus the static domain context (system-prompt
snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * XeroTokens / XeroTenant / XeroWebhookEvent — domain records.
  * IXeroBoard        — token / tenant / webhook board.
  * InMemoryXeroBoard — thread-safe in-memory board.
  * CommerceIntegrationXeroDomainContext — static system-prompt + metadata.

Note: the C# ``CommerceIntegrationXeroCompanionAdapter`` decorates ``CircleAI.
Companion.ICompanionSession``, which is not part of the ported Python companion
surface, so it is intentionally not ported here.
"""
from __future__ import annotations

from .commerce_integration_xero_domain_context import (
    CommerceIntegrationXeroDomainContext,
)
from .xero_primitives import (
    IXeroBoard,
    InMemoryXeroBoard,
    XeroTenant,
    XeroTokens,
    XeroWebhookEvent,
)

__all__ = [
    "XeroTokens",
    "XeroTenant",
    "XeroWebhookEvent",
    "IXeroBoard",
    "InMemoryXeroBoard",
    "CommerceIntegrationXeroDomainContext",
]
