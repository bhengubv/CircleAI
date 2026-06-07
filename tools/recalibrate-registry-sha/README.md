# recalibrate-registry-sha

Anti-rot tool for the CircleAI model catalog. Downloads each entry in `src/CircleAI.Core/registry.json` (and updates the parallel `src/CircleAI.Core/Models/embedded_registry.json`), verifies the bytes are a real model weight, and pins the actual SHA-256 + byte length into the registry.

## Why this exists

The original catalog pins were derived from ModelScope's file-listing API without ever downloading a single file. Those hashes do not match the bytes ModelScope actually serves, so `ModelDownloadService` correctly refuses to load any model on a clean host.

Re-running this tool after a model update keeps the pins honest, so a clean host can always load and run every catalog entry.

## What it does per entry

1. **Sniff** — issues a Range request for the first 1 KB and rejects HTML error pages, git-LFS pointer files, and JSON error wrappers. A 450 MB "weight" that's actually a 200-byte LFS pointer or a `<!DOCTYPE html>` login page is detected before the full download starts.
2. **Stream + hash PrimaryUrl** — streams the file end-to-end with a 1 MB chunk buffer, hashing as it goes, with periodic progress (MB/s + ETA).
3. **Stream + hash FallbackUrl** — same. The catalog's "both URLs return the same bytes" claim is verified, not trusted: if PrimaryUrl and FallbackUrl produce different SHA-256s, the entry is rejected and the registry is not updated.
4. **Atomic write** — only when EVERY requested entry verifies, the tool backs up the registries (`*.bak`) and writes the new pins (`Checksum` and `SizeBytes`).

## Usage

```bash
# verify + update every entry in the catalog
dotnet run --project tools/recalibrate-registry-sha

# verify + update one or more specific entries
dotnet run --project tools/recalibrate-registry-sha -- Qwen3-0.6B-MNN Qwen2.5-0.5B-Instruct-MNN
```

Exit code:
- `0` — every requested entry verified, pins updated
- `1` — one or more entries failed verification (good entries are still written)
- `2` — repo root / registry file / parse error (no entries processed)

## Notes

- The tool sends a real browser `User-Agent` because ModelScope's CDN (`resolve/master` URLs) returns 403 to clients with no UA. The runtime `ModelDownloadService` was patched to do the same so PrimaryUrl + FallbackUrl both work in production.
- Downloads land in `%TEMP%/circleai-recalibrate/`. They are kept after verification so consecutive runs can be cheap; delete the temp directory to force a re-download.
- Total catalog weight is ~30 GB. Plan accordingly; running the full set on a slow link can take hours.
