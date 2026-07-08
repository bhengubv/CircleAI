// prefix_cache.go
//
// Ports CircleAI.Inference.PrefixCacheService (PrefixCacheService.cs).
//
// RT-06 cross-session prefix cache. Snapshot the model's KV state once per
// (modelId, systemPrompt) pair, reload it on the next chat with the same pair,
// and skip the system-prompt prefill. This Go port owns the *indexing* only —
// the on-disk `.session` payload is whatever the native engine writes; here a
// caller (or the deterministic local generator) writes/reads bytes at PathFor.
//
// Cache layout mirrors C#:
//   <root>/<key>.session   ← KV snapshot bytes
// Eviction: LRU by file mtime, cap 500 MB, oldest-first.

package circleai

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// prefixCacheCapBytes mirrors PrefixCacheService.CapBytes (500 MB).
const prefixCacheCapBytes = 500 * 1024 * 1024

// PrefixCacheService manages an on-disk cache of "warm" model sessions keyed by
// the hash of (modelId, systemPrompt). Thread-safe. Ports
// CircleAI.Inference.PrefixCacheService.
type PrefixCacheService struct {
	root    string
	ioMutex sync.Mutex
}

var (
	defaultPrefixCacheOnce sync.Once
	defaultPrefixCache     *PrefixCacheService
)

// DefaultPrefixCacheService is the shared per-app instance rooted at
// %LOCALAPPDATA%/CircleAI/prefix-cache on Windows and ~/.circleai/prefix-cache
// elsewhere. Mirrors PrefixCacheService.Default (lazily constructed).
func DefaultPrefixCacheService() *PrefixCacheService {
	defaultPrefixCacheOnce.Do(func() {
		svc, err := NewPrefixCacheService(defaultPrefixCacheRoot())
		if err != nil {
			// Fall back to a temp-dir root rather than nil so Default is always usable.
			svc, _ = NewPrefixCacheService(filepath.Join(os.TempDir(), "CircleAI", "prefix-cache"))
		}
		defaultPrefixCache = svc
	})
	return defaultPrefixCache
}

// NewPrefixCacheService constructs a cache rooted at root. The directory is
// created on demand. Ports the C# constructor.
func NewPrefixCacheService(root string) (*PrefixCacheService, error) {
	if strings.TrimSpace(root) == "" {
		return nil, errors.New("root is required")
	}
	if err := os.MkdirAll(root, 0o755); err != nil {
		return nil, err
	}
	return &PrefixCacheService{root: root}, nil
}

// PrefixCacheKeyFor computes the cache key for a (modelId, systemPrompt) pair.
// Returns "" (no key) when systemPrompt is empty — there is nothing to cache
// without a system prompt to key against. Ports PrefixCacheService.KeyFor.
func PrefixCacheKeyFor(modelID, systemPrompt string) string {
	if strings.TrimSpace(modelID) == "" {
		return ""
	}
	if systemPrompt == "" {
		return ""
	}
	modelHash := sha256Hex(modelID)
	systemHash := sha256Hex(systemPrompt)
	// First 16 hex chars per component — collision-free at any single device's
	// cache scale, much shorter on disk.
	return modelHash[:16] + "_" + systemHash[:16]
}

// PathFor returns the cache path for key. The path may or may not exist.
func (s *PrefixCacheService) PathFor(key string) string {
	return filepath.Join(s.root, key+".session")
}

// HasEntry reports whether a cached entry exists for key.
func (s *PrefixCacheService) HasEntry(key string) bool {
	_, err := os.Stat(s.PathFor(key))
	return err == nil
}

// Touch updates the entry's mtime so LRU eviction treats it as recently used.
// Called after a successful load. No-op when the entry is absent.
func (s *PrefixCacheService) Touch(key string) {
	path := s.PathFor(key)
	if _, err := os.Stat(path); err == nil {
		now := time.Now().UTC()
		_ = os.Chtimes(path, now, now)
	}
}

// EvictIfNeeded evicts oldest entries until the directory is under the 500 MB
// cap. Best-effort — individual delete failures are swallowed. Ports
// EvictIfNeededAsync.
func (s *PrefixCacheService) EvictIfNeeded() {
	s.ioMutex.Lock()
	defer s.ioMutex.Unlock()

	entries, err := os.ReadDir(s.root)
	if err != nil {
		return
	}

	type sessionFile struct {
		path  string
		size  int64
		mtime time.Time
	}
	files := make([]sessionFile, 0, len(entries))
	var total int64
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".session") {
			continue
		}
		info, ierr := e.Info()
		if ierr != nil {
			continue
		}
		files = append(files, sessionFile{
			path:  filepath.Join(s.root, e.Name()),
			size:  info.Size(),
			mtime: info.ModTime().UTC(),
		})
		total += info.Size()
	}

	sort.Slice(files, func(i, j int) bool { return files[i].mtime.Before(files[j].mtime) })

	for i := 0; total > prefixCacheCapBytes && i < len(files); i++ {
		f := files[i]
		if err := os.Remove(f.path); err == nil {
			total -= f.size
		}
	}
}

// ── helpers ────────────────────────────────────────────────────────────────

func sha256Hex(input string) string {
	sum := sha256.Sum256([]byte(input))
	return hex.EncodeToString(sum[:])
}

func defaultPrefixCacheRoot() string {
	if local := strings.TrimSpace(os.Getenv("LOCALAPPDATA")); local != "" {
		return filepath.Join(local, "CircleAI", "prefix-cache")
	}
	home, err := os.UserHomeDir()
	if err != nil || home == "" {
		return filepath.Join(os.TempDir(), ".circleai", "prefix-cache")
	}
	return filepath.Join(home, ".circleai", "prefix-cache")
}
