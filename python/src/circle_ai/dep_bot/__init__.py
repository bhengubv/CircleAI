"""circle_ai.dep_bot — port of the CircleAI.DepBot assembly.

(2.9.0 contracts / 3.3.0 in-memory) Dependency-bot surface: a filesystem manifest
scanner (npm / pypi / cargo / nuget) and a text-rewrite updater that edits
manifests in place, plus fail-safe null defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    Dependency,
    DependencyUpdate,
    IDependencyAnalyzer,
    IDependencyUpdater,
)
from .in_memory_dep_bot import (
    FilesystemDependencyAnalyzer,
    TextRewriteDependencyUpdater,
)
from .null_implementations import (
    NullDependencyAnalyzer,
    NullDependencyUpdater,
)

__all__ = [
    "Dependency",
    "DependencyUpdate",
    "IDependencyAnalyzer",
    "IDependencyUpdater",
    "FilesystemDependencyAnalyzer",
    "TextRewriteDependencyUpdater",
    "NullDependencyAnalyzer",
    "NullDependencyUpdater",
]
