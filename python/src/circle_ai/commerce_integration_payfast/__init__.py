"""circle_ai.commerce_integration_payfast — port of the
CircleAI.Commerce.Integration.PayFast assembly.

(3.3.0) PayFast integration primitives — real MD5 signature builder (byte-for-
byte with the C# WebUtility.UrlEncode + passphrase scheme), ITN merchant
validation, in-memory webhook recorder — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * PayFastConfig / PayFastItnPayload — domain records.
  * IPayFastBoard        — signature / ITN / webhook board.
  * InMemoryPayFastBoard — thread-safe in-memory board.
  * CommerceIntegrationPayFastDomainContext — static system-prompt + metadata.

Note: the C# ``CommerceIntegrationPayFastCompanionAdapter`` decorates
``CircleAI.Companion.ICompanionSession``, which is not part of the ported Python
companion surface, so it is intentionally not ported here.
"""
from __future__ import annotations

from .commerce_integration_payfast_domain_context import (
    CommerceIntegrationPayFastDomainContext,
)
from .payfast_primitives import (
    IPayFastBoard,
    InMemoryPayFastBoard,
    PayFastConfig,
    PayFastItnPayload,
)

__all__ = [
    "PayFastConfig",
    "PayFastItnPayload",
    "IPayFastBoard",
    "InMemoryPayFastBoard",
    "CommerceIntegrationPayFastDomainContext",
]
