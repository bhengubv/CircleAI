"""conftest.py — pytest configuration for the circle-ai-sdk test suite.

Adds src/ to sys.path so tests can import circle_ai without installing the package.
"""
from __future__ import annotations

import sys
import pathlib

# Allow imports from src/ directory without pip install
_SRC = pathlib.Path(__file__).parent.parent / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

# Fixtures directory (root-level, shared across all language implementations)
FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"
