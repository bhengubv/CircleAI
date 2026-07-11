# in_memory_dep_bot.py
#
# Port of CircleAI.DepBot InMemoryDepBot.cs (C# — the EXACT spec).
#
# (3.3.0) Real IDependencyAnalyzer + IDependencyUpdater that scan a repo on disk
# for known manifests (package.json, requirements.txt, Cargo.toml, *.csproj).
# Updates apply real edits to the manifest files.
#
#   • FilesystemDependencyAnalyzer.scan_async — walk the repo, parse each manifest
#     kind exactly as the C# does (npm deps + devDeps, pypi requirements lines,
#     cargo [dependencies] section, nuget PackageReference).
#   • TextRewriteDependencyUpdater — propose returns empty (no invented latest);
#     apply rewrites the matching manifest entry per ecosystem.

from __future__ import annotations

import json
import os
import re
from typing import List, Optional

from .contracts import Dependency, DependencyUpdate, IDependencyAnalyzer, IDependencyUpdater

_REQ_RX = re.compile(r"^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?")
_CARGO_RX = re.compile(r'^([A-Za-z0-9_\-]+)\s*=\s*"([^"]+)"')
_CSPROJ_RX = re.compile(r'<PackageReference\s+Include="(?P<name>[^"]+)"\s+Version="(?P<ver>[^"]+)"')


def _walk(root: str, name: Optional[str] = None, ext: Optional[str] = None) -> List[str]:
    out: List[str] = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in filenames:
            if name is not None and fn != name:
                continue
            if ext is not None and not fn.endswith(ext):
                continue
            out.append(os.path.join(dirpath, fn))
    return out


class FilesystemDependencyAnalyzer(IDependencyAnalyzer):
    """Real filesystem :class:`IDependencyAnalyzer`. Mirrors
    ``CircleAI.DepBot.FilesystemDependencyAnalyzer``."""

    @property
    def backend_id(self) -> str:
        return "filesystem"

    async def scan_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[Dependency]:
        if repo_path is None or repo_path.strip() == "":
            raise ValueError("repoPath required")
        if not os.path.isdir(repo_path):
            raise NotADirectoryError(repo_path)

        results: List[Dependency] = []

        # npm / yarn
        for pkg in _walk(repo_path, name="package.json"):
            if "node_modules" in pkg:
                continue
            try:
                with open(pkg, "r", encoding="utf-8") as fh:
                    doc = json.load(fh)
                if isinstance(doc, dict):
                    for key in ("dependencies", "devDependencies"):
                        section = doc.get(key)
                        if not isinstance(section, dict):
                            continue
                        for name, ver in section.items():
                            results.append(
                                Dependency("npm", name, ver if isinstance(ver, str) else "", None)
                            )
            except Exception:
                # skip malformed file (mirror C# catch-and-continue)
                continue

        # Python — requirements.txt
        for req in _walk(repo_path, name="requirements.txt"):
            try:
                with open(req, "r", encoding="utf-8") as fh:
                    lines = fh.read().split("\n")
            except OSError:
                continue
            for raw_line in lines:
                line = raw_line.strip()
                if len(line) == 0 or line.startswith("#"):
                    continue
                match = _REQ_RX.match(line)
                if match is None:
                    continue
                results.append(
                    Dependency("pypi", match.group(1), match.group(3) or "", None)
                )

        # Rust — Cargo.toml [dependencies]
        for toml in _walk(repo_path, name="Cargo.toml"):
            if "target" in toml:
                continue
            try:
                with open(toml, "r", encoding="utf-8") as fh:
                    lines = fh.read().split("\n")
            except OSError:
                continue
            in_deps = False
            for raw_line in lines:
                line = raw_line.strip()
                if line.startswith("["):
                    in_deps = line.lower() == "[dependencies]"
                    continue
                if not in_deps or len(line) == 0 or line.startswith("#"):
                    continue
                match = _CARGO_RX.match(line)
                if match is None:
                    continue
                results.append(Dependency("cargo", match.group(1), match.group(2), None))

        # .NET — *.csproj <PackageReference Include="X" Version="Y" />
        for csproj in _walk(repo_path, ext=".csproj"):
            try:
                with open(csproj, "r", encoding="utf-8") as fh:
                    text = fh.read()
            except OSError:
                continue
            for m in _CSPROJ_RX.finditer(text):
                results.append(Dependency("nuget", m.group("name"), m.group("ver"), None))

        return results


class TextRewriteDependencyUpdater(IDependencyUpdater):
    """Text-rewrite :class:`IDependencyUpdater`. Mirrors
    ``CircleAI.DepBot.TextRewriteDependencyUpdater``."""

    @property
    def backend_id(self) -> str:
        return "text-rewrite"

    async def propose_updates_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[DependencyUpdate]:
        # Surfaces nothing without a registry — never invents a fake latest.
        if repo_path is None or repo_path.strip() == "":
            raise ValueError("repoPath required")
        return []

    async def apply_update_async(
        self, repo_path: str, update: DependencyUpdate, ct: Optional[object] = None
    ) -> None:
        if update is None:
            raise ValueError("update")
        if repo_path is None or repo_path.strip() == "":
            raise ValueError("repoPath required")
        if not os.path.isdir(repo_path):
            raise NotADirectoryError(repo_path)

        eco = update.ecosystem.lower()
        if eco == "nuget":
            for csproj in _walk(repo_path, ext=".csproj"):
                with open(csproj, "r", encoding="utf-8") as fh:
                    text = fh.read()
                pattern = (
                    r'<PackageReference\s+Include="'
                    + re.escape(update.name)
                    + r'"\s+Version="[^"]+"'
                )
                replacement = f'<PackageReference Include="{update.name}" Version="{update.to_version}"'
                updated = re.sub(pattern, replacement, text)
                if updated != text:
                    with open(csproj, "w", encoding="utf-8") as fh:
                        fh.write(updated)
        elif eco == "npm":
            for pkg in _walk(repo_path, name="package.json"):
                if "node_modules" in pkg:
                    continue
                with open(pkg, "r", encoding="utf-8") as fh:
                    js = fh.read()
                pattern = r'"' + re.escape(update.name) + r'"\s*:\s*"[^"]+"'
                replacement = f'"{update.name}": "{update.to_version}"'
                with open(pkg, "w", encoding="utf-8") as fh:
                    fh.write(re.sub(pattern, replacement, js))
        elif eco == "pypi":
            for req in _walk(repo_path, name="requirements.txt"):
                with open(req, "r", encoding="utf-8") as fh:
                    lines = fh.read().split("\n")
                for i in range(len(lines)):
                    line = lines[i].strip()
                    if line.startswith("#") or len(line) == 0:
                        continue
                    m = re.match(
                        r"^" + re.escape(update.name) + r"\s*[=<>!~]=?\s*[0-9.A-Za-z_\-]+",
                        line,
                    )
                    if m is not None:
                        lines[i] = f"{update.name}=={update.to_version}"
                with open(req, "w", encoding="utf-8") as fh:
                    fh.write("\n".join(lines))
