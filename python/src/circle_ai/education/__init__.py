"""circle_ai.education — port of the CircleAI.Education assembly.

(3.3.0) Real domain types + in-memory board for the Education vertical: courses,
lessons, student records + progress — plus the static domain context
(system-prompt snippet, compliance flags, suggested tools). C# is the exact spec.

Public surface:

  * Course / Lesson / StudentRecord — domain records.
  * IEducationBoard        — course / lesson / student board.
  * InMemoryEducationBoard — thread-safe in-memory board.
  * EducationDomainContext — static system-prompt + compliance/tool metadata.

Note: the C# ``EducationCompanionAdapter`` decorates ``CircleAI.Companion.
ICompanionSession``, which is not part of the ported Python companion surface,
so it is intentionally not ported here.
"""
from __future__ import annotations

from .education_domain_context import EducationDomainContext
from .education_primitives import (
    Course,
    IEducationBoard,
    InMemoryEducationBoard,
    Lesson,
    StudentRecord,
)

__all__ = [
    "Course",
    "Lesson",
    "StudentRecord",
    "IEducationBoard",
    "InMemoryEducationBoard",
    "EducationDomainContext",
]
