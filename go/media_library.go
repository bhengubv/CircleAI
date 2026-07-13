// media_library.go
//
// Ports the CircleAI.Media primitive vertical (MediaPrimitives.cs):
//   MediaKind             -> MediaKind (int consts, ordinals Audio=0/Video=1/Image=2)
//   MediaAsset (record)   -> MediaAsset (value struct; TimeSpan? Duration -> *time.Duration)
//   IMediaLibrary         -> MediaLibrary interface
//   InMemoryMediaLibrary  -> InMemoryMediaLibrary
//
// FLAT-PACKAGE DISAMBIGUATION: CircleAI.MediaHub also declares IMediaLibrary /
// InMemoryMediaLibrary / a media asset type. To coexist in one Go package the
// Media (asset-catalog) vertical keeps the concrete names InMemoryMediaLibrary
// and MediaAsset, while the MediaHub (server) vertical is ported with a "Hub"
// prefix in media_hub.go (HubMediaLibrary / HubMediaItem / InMemoryHubMediaLibrary).
// The IMediaLibrary *interface* is named MediaLibrary here; MediaHub's is
// HubMediaLibrary.
//
// DETERMINISM: the C# reference orders results by CreatedAtUtc descending over a
// ConcurrentDictionary whose enumeration order is unspecified, so equal-timestamp
// ties are non-deterministic there. This port sorts CreatedAtUtc-desc with a
// stable AssetId-asc tiebreak so identical inputs always yield identical output
// (same primary ordering as C#, deterministic on ties).

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// MediaKind classifies a media asset. Ordinals match the C# enum
// (Audio=0, Video=1, Image=2). Ports MediaKind.
type MediaKind int

const (
	// MediaKindAudio is an audio asset.
	MediaKindAudio MediaKind = iota
	// MediaKindVideo is a video asset.
	MediaKindVideo
	// MediaKindImage is an image asset.
	MediaKindImage
)

// String renders the C# enum member name for a MediaKind.
func (k MediaKind) String() string {
	switch k {
	case MediaKindAudio:
		return "Audio"
	case MediaKindVideo:
		return "Video"
	case MediaKindImage:
		return "Image"
	default:
		return "Unknown"
	}
}

// MediaAsset is one entry in a media asset catalog (audio / video / image).
// Ports the MediaAsset record. It is treated as an immutable value: copy freely,
// never mutate a stored asset in place. Duration is a pointer to mirror the
// nullable C# TimeSpan? (nil == no duration, e.g. for images).
type MediaAsset struct {
	// AssetId is the catalog-unique identifier (required, non-blank).
	AssetId string
	// Title is the human-readable title (searched by substring).
	Title string
	// Kind is the media classification.
	Kind MediaKind
	// Duration is the play length; nil for assets with no duration (e.g. images).
	Duration *time.Duration
	// Bytes is the asset size in bytes.
	Bytes int64
	// Mime is the MIME type (e.g. "audio/mpeg").
	Mime string
	// CreatedAtUtc is the creation timestamp (UTC); the ordering key for listings.
	CreatedAtUtc time.Time
}

// MediaLibrary is a catalog of media assets. Ports the CircleAI.Media
// IMediaLibrary interface (named MediaLibrary to avoid colliding with the
// MediaHub IMediaLibrary, ported as HubMediaLibrary).
type MediaLibrary interface {
	// Add stores (or replaces by AssetId) an asset. Returns an error when
	// AssetId is blank (ports the ArgumentException the C# Add throws).
	Add(a MediaAsset) error
	// Get returns the asset for id and true, or (zero, false) when absent.
	Get(id string) (MediaAsset, bool)
	// Remove drops an asset by id, returning true if it was present.
	Remove(id string) bool
	// Count returns the number of assets currently catalogued.
	Count() int
	// TotalBytes returns the summed Bytes of every catalogued asset.
	TotalBytes() int64
	// ListByKind returns all assets of kind, newest first (CreatedAtUtc desc).
	ListByKind(kind MediaKind) []MediaAsset
	// ByMime returns assets whose MIME starts with mimePrefix (case-insensitive),
	// newest first. Empty prefix yields nothing.
	ByMime(mimePrefix string) []MediaAsset
	// Search returns up to topK assets whose Title contains q (case-insensitive),
	// newest first. Returns an error when topK <= 0 (ports ArgumentOutOfRangeException).
	Search(q string, topK int) ([]MediaAsset, error)
}

// InMemoryMediaLibrary is a concurrency-safe in-memory MediaLibrary backed by a
// map keyed on AssetId with ordinal (case-sensitive) comparison, matching the
// C# ConcurrentDictionary<string, MediaAsset>(StringComparer.Ordinal). Ports
// InMemoryMediaLibrary.
type InMemoryMediaLibrary struct {
	mu    sync.RWMutex
	items map[string]MediaAsset
}

// NewInMemoryMediaLibrary constructs an empty library.
func NewInMemoryMediaLibrary() *InMemoryMediaLibrary {
	return &InMemoryMediaLibrary{items: make(map[string]MediaAsset)}
}

// Add stores or replaces a by AssetId. Blank AssetId is rejected (ports the C#
// ArgumentException "AssetId required"; the C# ArgumentNullException on a null
// record has no Go analogue since MediaAsset is a value type).
func (l *InMemoryMediaLibrary) Add(a MediaAsset) error {
	if strings.TrimSpace(a.AssetId) == "" {
		return errors.New("AssetId required")
	}
	l.mu.Lock()
	l.items[a.AssetId] = a
	l.mu.Unlock()
	return nil
}

// Get returns the asset for id and true, or (zero, false) if not present
// (ports GetValueOrDefault, where a missing key yields the default/absent case).
func (l *InMemoryMediaLibrary) Get(id string) (MediaAsset, bool) {
	l.mu.RLock()
	a, ok := l.items[id]
	l.mu.RUnlock()
	return a, ok
}

// Remove drops an asset by id, returning true if it was present. Blank id
// returns false. Ports InMemoryMediaLibrary.Remove (TryRemove).
func (l *InMemoryMediaLibrary) Remove(id string) bool {
	if id == "" {
		return false
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	_, ok := l.items[id]
	delete(l.items, id)
	return ok
}

// Count returns the number of assets currently catalogued. Ports
// InMemoryMediaLibrary.Count.
func (l *InMemoryMediaLibrary) Count() int {
	l.mu.RLock()
	defer l.mu.RUnlock()
	return len(l.items)
}

// TotalBytes returns the summed Bytes of every catalogued asset. Ports
// InMemoryMediaLibrary.TotalBytes (Sum(a => a.Bytes)).
func (l *InMemoryMediaLibrary) TotalBytes() int64 {
	l.mu.RLock()
	defer l.mu.RUnlock()
	var total int64
	for _, a := range l.items {
		total += a.Bytes
	}
	return total
}

// ListByKind returns every asset of kind ordered CreatedAtUtc descending
// (newest first). Ports the LINQ Where(Kind==kind).OrderByDescending(CreatedAtUtc).
func (l *InMemoryMediaLibrary) ListByKind(kind MediaKind) []MediaAsset {
	l.mu.RLock()
	out := make([]MediaAsset, 0)
	for _, a := range l.items {
		if a.Kind == kind {
			out = append(out, a)
		}
	}
	l.mu.RUnlock()
	sortByCreatedDesc(out)
	return out
}

// ByMime returns assets whose MIME type starts with mimePrefix (e.g. "image/",
// "audio/"), matched case-insensitively and ordered CreatedAtUtc descending.
// Empty prefix yields nothing. Ports InMemoryMediaLibrary.ByMime
// (Where(Mime.StartsWith(prefix, OrdinalIgnoreCase)).OrderByDescending(CreatedAtUtc)).
func (l *InMemoryMediaLibrary) ByMime(mimePrefix string) []MediaAsset {
	if mimePrefix == "" {
		return []MediaAsset{}
	}
	needle := strings.ToLower(mimePrefix)
	l.mu.RLock()
	out := make([]MediaAsset, 0)
	for _, a := range l.items {
		if strings.HasPrefix(strings.ToLower(a.Mime), needle) {
			out = append(out, a)
		}
	}
	l.mu.RUnlock()
	sortByCreatedDesc(out)
	return out
}

// Search returns up to topK assets whose Title contains q (case-insensitive,
// ordinal), ordered CreatedAtUtc descending. topK must be positive. Ports the
// LINQ Where(Title.Contains(q, OrdinalIgnoreCase)).OrderByDescending(CreatedAtUtc).Take(topK).
func (l *InMemoryMediaLibrary) Search(q string, topK int) ([]MediaAsset, error) {
	if topK <= 0 {
		return nil, errors.New("topK must be positive")
	}
	needle := strings.ToLower(q)
	l.mu.RLock()
	hits := make([]MediaAsset, 0)
	for _, a := range l.items {
		if strings.Contains(strings.ToLower(a.Title), needle) {
			hits = append(hits, a)
		}
	}
	l.mu.RUnlock()
	sortByCreatedDesc(hits)
	if len(hits) > topK {
		hits = hits[:topK]
	}
	return hits, nil
}

// sortByCreatedDesc orders assets by CreatedAtUtc descending with a stable
// AssetId-ascending tiebreak (see the DETERMINISM note in the file header).
func sortByCreatedDesc(assets []MediaAsset) {
	sort.SliceStable(assets, func(i, j int) bool {
		if !assets[i].CreatedAtUtc.Equal(assets[j].CreatedAtUtc) {
			return assets[i].CreatedAtUtc.After(assets[j].CreatedAtUtc)
		}
		return assets[i].AssetId < assets[j].AssetId
	})
}

// Interface guard.
var _ MediaLibrary = (*InMemoryMediaLibrary)(nil)
