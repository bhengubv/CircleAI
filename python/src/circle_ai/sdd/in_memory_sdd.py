# in_memory_sdd.py
#
# Port of CircleAI.SDD InMemorySDD.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory specification store + JSON-schema-shape validator +
# hello-world language scaffolder (C#, TypeScript, Python).
#
#   • InMemorySpecificationStore — thread-safe dict store.
#   • JsonShapeSpecificationValidator — Title/Body required; if Schema present it
#     must parse as JSON, be an object, and declare a top-level "type".
#   • HelloWorldSpecToScaffold — emit a minimal compilable project per language;
#     unsupported language raises (mirrors C# NotSupportedException).

from __future__ import annotations

import json
import threading
from typing import Dict, List, Optional

from .contracts import (
    ISpecToScaffold,
    ISpecificationStore,
    ISpecificationValidator,
    ScaffoldedProject,
    SpecValidationResult,
    Specification,
)


class InMemorySpecificationStore(ISpecificationStore):
    """Thread-safe in-memory :class:`ISpecificationStore`. Mirrors
    ``CircleAI.SDD.InMemorySpecificationStore``."""

    def __init__(self) -> None:
        self._items: Dict[str, Specification] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def upsert_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> None:
        if spec is None:
            raise ValueError("spec")
        if spec.spec_id is None or spec.spec_id.strip() == "":
            raise ValueError("SpecId required")
        with self._lock:
            self._items[spec.spec_id] = spec

    async def get_async(
        self, spec_id: str, ct: Optional[object] = None
    ) -> Optional[Specification]:
        if spec_id is None or spec_id.strip() == "":
            raise ValueError("specId required")
        with self._lock:
            return self._items.get(spec_id)

    async def list_async(
        self, ct: Optional[object] = None
    ) -> List[Specification]:
        with self._lock:
            return list(self._items.values())


class JsonShapeSpecificationValidator(ISpecificationValidator):
    """JSON-shape :class:`ISpecificationValidator`. Mirrors
    ``CircleAI.SDD.JsonShapeSpecificationValidator``."""

    @property
    def backend_id(self) -> str:
        return "json-shape"

    async def validate_async(
        self, spec: Specification, ct: Optional[object] = None
    ) -> SpecValidationResult:
        if spec is None:
            raise ValueError("spec")
        errors: List[str] = []
        if spec.title is None or spec.title.strip() == "":
            errors.append("Title is required.")
        if spec.body is None or spec.body.strip() == "":
            errors.append("Body is required.")
        if spec.schema is not None and spec.schema.strip() != "":
            try:
                doc = json.loads(spec.schema)
                if not isinstance(doc, dict):
                    errors.append("Schema must be a JSON object.")
                elif "type" not in doc:
                    errors.append("Schema must declare a top-level 'type'.")
            except json.JSONDecodeError as ex:
                errors.append(f"Schema is not valid JSON: {ex}")
        return SpecValidationResult(len(errors) == 0, errors)


class HelloWorldSpecToScaffold(ISpecToScaffold):
    """Hello-world :class:`ISpecToScaffold`. Mirrors
    ``CircleAI.SDD.HelloWorldSpecToScaffold``."""

    @property
    def backend_id(self) -> str:
        return "hello-world"

    async def scaffold_async(
        self, spec: Specification, target_language: str, ct: Optional[object] = None
    ) -> ScaffoldedProject:
        if spec is None:
            raise ValueError("spec")
        if target_language is None or target_language.strip() == "":
            raise ValueError("targetLanguage required")

        files: Dict[str, bytes] = {}
        lang = target_language.lower()
        name = self._sanitize_name(spec.spec_id)

        if lang in ("csharp", "c#"):
            files["Program.cs"] = self._bytes(
                f'Console.WriteLine("{name}: {self._escape(spec.title)}");\n'
            )
            files[f"{name}.csproj"] = self._bytes(
                '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>'
                "<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>"
                "<Nullable>enable</Nullable></PropertyGroup>\n</Project>\n"
            )
            files["README.md"] = self._bytes(
                f"# {self._escape(spec.title)}\n\n{self._escape(spec.body)}\n"
            )
        elif lang in ("typescript", "ts"):
            files["index.ts"] = self._bytes(
                f'console.log("{name}: {self._escape(spec.title)}");\n'
            )
            files["package.json"] = self._bytes(
                f'{{"name":"{name}","version":"0.1.0","main":"index.ts",'
                '"scripts":{"start":"ts-node index.ts"}}\n'
            )
            files["tsconfig.json"] = self._bytes(
                '{"compilerOptions":{"strict":true,"target":"ES2022","module":"commonjs"}}\n'
            )
            files["README.md"] = self._bytes(
                f"# {self._escape(spec.title)}\n\n{self._escape(spec.body)}\n"
            )
        elif lang in ("python", "py"):
            files["main.py"] = self._bytes(
                f'def main():\n    print("{name}: {self._escape(spec.title)}")\n\n'
                'if __name__ == "__main__":\n    main()\n'
            )
            files["pyproject.toml"] = self._bytes(
                f'[project]\nname = "{name}"\nversion = "0.1.0"\n'
                'requires-python = ">=3.10"\n'
            )
            files["README.md"] = self._bytes(
                f"# {self._escape(spec.title)}\n\n{self._escape(spec.body)}\n"
            )
        else:
            raise ValueError(
                f"Language '{target_language}' is not supported by "
                "HelloWorldSpecToScaffold (csharp / typescript / python)."
            )

        return ScaffoldedProject(f"{name}-{lang}", files)

    @staticmethod
    def _sanitize_name(spec_id: str) -> str:
        if spec_id is None or spec_id.strip() == "":
            return "project"
        out = [ch for ch in spec_id if ch.isalnum() or ch in ("_", "-")]
        return "project" if len(out) == 0 else "".join(out)

    @staticmethod
    def _escape(s: str) -> str:
        return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")

    @staticmethod
    def _bytes(s: str) -> bytes:
        return s.encode("utf-8")
