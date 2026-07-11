// runtime_native.go
//
// Ports CircleAI.Runtime.NativeRuntimes (NativeRuntimeBundle.cs +
// INativeRuntimeFetcher.cs + NativeRuntimeRegistry.cs + NativeRuntimeFetcher.cs):
//
//	NativeRuntimeBundle / NativeRuntimeInstall (records) -> value structs
//	INativeRuntimeFetcher                                -> NativeRuntimeFetcher
//	NativeRuntimeRegistry                                -> NativeRuntimeRegistry
//	NativeRuntimeFetcher (class)                          -> InMemoryNativeRuntimeFetcher
//
// The C# NativeRuntimeFetcher performs real HttpClient download + SHA-256 verify
// + archive extraction to a cache directory. That is inherently
// network + filesystem I/O; per the port NOTE ("any external store/network is
// injected"), the fetcher's external effect is abstracted behind an injected
// RuntimeContentStore and the in-memory fetcher becomes fully deterministic:
//   - the registry (bundle lookup by (os, arch, backend), newest-version-wins)
//     is ported exactly, plus a dependency-free JSON loader (encoding/json in
//     place of System.Text.Json);
//   - InMemoryNativeRuntimeFetcher resolves a bundle, asks the injected store to
//     materialise it, and returns the NativeRuntimeInstall — reproducing
//     EnsureRuntimeAsync's contract (fast-path cache hit, error on unknown
//     tuple, progress 1.0 on completion) without touching the network or disk.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
)

// NativeRuntimeBundle describes one fetchable MNN runtime archive for an
// (OS, arch, backend) tuple. Ports the NativeRuntimeBundle record. FallbackURI
// and ArchiveSha256Hex are empty when unset (C# nullable).
type NativeRuntimeBundle struct {
	MnnVersion         string
	Os                 OperatingSystemKind
	Arch               ArchitectureKind
	Backend            BackendKind
	PrimaryURI         string
	FallbackURI        string
	ArchiveSha256Hex   string
	MnnCoreLibraryName string
}

// NativeRuntimeInstall is the result of a successful EnsureRuntime. Ports the
// NativeRuntimeInstall record.
type NativeRuntimeInstall struct {
	Bundle        NativeRuntimeBundle
	ExtractedRoot string
	MnnCorePath   string
}

// NativeRuntimeFetcher ensures MNN runtime bundles are present. Ports
// INativeRuntimeFetcher. Progress is reported through an optional callback in
// [0.0, 1.0] (nil to disable), replacing the C# IProgress<double>.
type NativeRuntimeFetcher interface {
	// EnsureRuntime ensures the bundle for (os, arch, backend) is materialised and
	// returns its install. Errors when no bundle is registered for the tuple.
	EnsureRuntime(ctx context.Context, os OperatingSystemKind, arch ArchitectureKind, backend BackendKind, progress func(float64)) (NativeRuntimeInstall, error)
	// IsRuntimeCached reports whether the bundle for the tuple is already present.
	IsRuntimeCached(ctx context.Context, os OperatingSystemKind, arch ArchitectureKind, backend BackendKind) (bool, error)
	// ListAvailableBundles lists the registry's bundles.
	ListAvailableBundles() []NativeRuntimeBundle
}

// RuntimeContentStore is the injected external effect the fetcher delegates to:
// it materialises a bundle (in the real host, download + verify + extract) and
// returns the extracted root + the located MNN core path. This is the seam that
// replaces the C# HttpClient + filesystem in the deterministic port.
type RuntimeContentStore interface {
	// Materialise returns the extracted root and MNN core path for bundle, or an
	// error. progress (when non-nil) may be reported in [0.0, 1.0].
	Materialise(ctx context.Context, bundle NativeRuntimeBundle, progress func(float64)) (extractedRoot, mnnCorePath string, err error)
	// IsMaterialised reports whether bundle is already present (no external I/O).
	IsMaterialised(ctx context.Context, bundle NativeRuntimeBundle) bool
}

// NativeRuntimeRegistry holds MNN runtime bundles + looks them up by tuple.
// Ports NativeRuntimeRegistry. Construct with NewNativeRuntimeRegistry or
// LoadNativeRuntimeRegistryJSON.
type NativeRuntimeRegistry struct {
	bundles []NativeRuntimeBundle
}

// NewNativeRuntimeRegistry constructs a registry from an explicit bundle list.
func NewNativeRuntimeRegistry(bundles []NativeRuntimeBundle) *NativeRuntimeRegistry {
	cp := make([]NativeRuntimeBundle, len(bundles))
	copy(cp, bundles)
	return &NativeRuntimeRegistry{bundles: cp}
}

// All returns all loaded bundles. Ports the All property.
func (r *NativeRuntimeRegistry) All() []NativeRuntimeBundle {
	out := make([]NativeRuntimeBundle, len(r.bundles))
	copy(out, r.bundles)
	return out
}

// Find returns the newest bundle matching (os, arch, backend) and true, or
// (zero, false). Ports Find — when several MNN versions match, the highest
// version string wins (ordinal string sort), exactly as in C#.
func (r *NativeRuntimeRegistry) Find(os OperatingSystemKind, arch ArchitectureKind, backend BackendKind) (NativeRuntimeBundle, bool) {
	var best NativeRuntimeBundle
	found := false
	for _, b := range r.bundles {
		if b.Os == os && b.Arch == arch && b.Backend == backend {
			if !found || b.MnnVersion > best.MnnVersion {
				best = b
				found = true
			}
		}
	}
	return best, found
}

// nativeRegistryJSON mirrors the embedded_native_registry.json shape the C#
// LoadFromStream parses (mnn_versions[].{version,bundles[]}).
type nativeRegistryJSON struct {
	MnnVersions []struct {
		Version string `json:"version"`
		Bundles []struct {
			Os          string `json:"os"`
			Arch        string `json:"arch"`
			Backend     string `json:"backend"`
			URL         string `json:"url"`
			FallbackURL string `json:"fallback_url"`
			Sha256      string `json:"sha256"`
			MnnLib      string `json:"mnn_lib"`
		} `json:"bundles"`
	} `json:"mnn_versions"`
}

// LoadNativeRuntimeRegistryJSON loads a registry from JSON bytes in the same
// schema as the C# embedded_native_registry.json. Ports LoadFromStream — entries
// with an unparseable os/arch/backend/url are skipped (tolerant parse), and a
// missing mnn_lib falls back to the per-OS default library name.
func LoadNativeRuntimeRegistryJSON(data []byte) (*NativeRuntimeRegistry, error) {
	var doc nativeRegistryJSON
	if err := json.Unmarshal(data, &doc); err != nil {
		return nil, err
	}
	list := make([]NativeRuntimeBundle, 0)
	for _, v := range doc.MnnVersions {
		for _, b := range v.Bundles {
			os, ok1 := parseOperatingSystemKind(b.Os)
			arch, ok2 := parseArchitectureKind(b.Arch)
			backend, ok3 := ParseBackendKind(b.Backend)
			if !ok1 || !ok2 || !ok3 || strings.TrimSpace(b.URL) == "" {
				continue
			}
			core := b.MnnLib
			if strings.TrimSpace(core) == "" {
				core = defaultCoreLibName(os)
			}
			list = append(list, NativeRuntimeBundle{
				MnnVersion:         v.Version,
				Os:                 os,
				Arch:               arch,
				Backend:            backend,
				PrimaryURI:         b.URL,
				FallbackURI:        b.FallbackURL,
				ArchiveSha256Hex:   b.Sha256,
				MnnCoreLibraryName: core,
			})
		}
	}
	return NewNativeRuntimeRegistry(list), nil
}

// defaultCoreLibName returns the default MNN core library name per OS. Ports
// DefaultCoreLibName.
func defaultCoreLibName(os OperatingSystemKind) string {
	switch os {
	case OSWindows:
		return "MNN.dll"
	case OSMacOS, OSIOS:
		return "MNN"
	default:
		return "libMNN.so"
	}
}

// InMemoryNativeRuntimeFetcher is a deterministic NativeRuntimeFetcher over a
// registry + an injected RuntimeContentStore. Ports NativeRuntimeFetcher's
// observable contract without network/disk I/O. Construct with
// NewInMemoryNativeRuntimeFetcher.
type InMemoryNativeRuntimeFetcher struct {
	registry *NativeRuntimeRegistry
	store    RuntimeContentStore
}

// NewInMemoryNativeRuntimeFetcher constructs the fetcher. Panics if registry or
// store is nil.
func NewInMemoryNativeRuntimeFetcher(registry *NativeRuntimeRegistry, store RuntimeContentStore) *InMemoryNativeRuntimeFetcher {
	if registry == nil {
		panic("registry must not be nil")
	}
	if store == nil {
		panic("store must not be nil")
	}
	return &InMemoryNativeRuntimeFetcher{registry: registry, store: store}
}

// ListAvailableBundles lists the registry's bundles. Ports ListAvailableBundles.
func (f *InMemoryNativeRuntimeFetcher) ListAvailableBundles() []NativeRuntimeBundle {
	return f.registry.All()
}

// IsRuntimeCached reports whether the tuple's bundle is already materialised.
// Ports IsRuntimeCachedAsync (no network I/O; false when the tuple is unknown).
func (f *InMemoryNativeRuntimeFetcher) IsRuntimeCached(ctx context.Context, os OperatingSystemKind, arch ArchitectureKind, backend BackendKind) (bool, error) {
	if err := ctx.Err(); err != nil {
		return false, err
	}
	bundle, ok := f.registry.Find(os, arch, backend)
	if !ok {
		return false, nil
	}
	return f.store.IsMaterialised(ctx, bundle), nil
}

// EnsureRuntime resolves the bundle, materialises it via the injected store, and
// returns the install. Ports EnsureRuntimeAsync — errors with the C#-style
// "No native runtime bundle registered for ..." message when the tuple is
// unknown, and reports progress 1.0 on completion.
func (f *InMemoryNativeRuntimeFetcher) EnsureRuntime(ctx context.Context, os OperatingSystemKind, arch ArchitectureKind, backend BackendKind, progress func(float64)) (NativeRuntimeInstall, error) {
	if err := ctx.Err(); err != nil {
		return NativeRuntimeInstall{}, err
	}
	bundle, ok := f.registry.Find(os, arch, backend)
	if !ok {
		return NativeRuntimeInstall{}, errors.New("No native runtime bundle registered for (" +
			operatingSystemKindName(os) + ", " + architectureKindName(arch) + ", " + backend.String() + ").")
	}
	root, corePath, err := f.store.Materialise(ctx, bundle, progress)
	if err != nil {
		return NativeRuntimeInstall{}, err
	}
	if progress != nil {
		progress(1.0)
	}
	return NativeRuntimeInstall{Bundle: bundle, ExtractedRoot: root, MnnCorePath: corePath}, nil
}

// MapRuntimeContentStore is a trivial deterministic RuntimeContentStore backed
// by a map from bundle key -> (extractedRoot, mnnCorePath). It is the in-memory
// stand-in for the real download+extract store, letting the fetcher be wired and
// exercised end-to-end. Construct with NewMapRuntimeContentStore.
type MapRuntimeContentStore struct {
	entries map[string]mapRuntimeEntry
}

type mapRuntimeEntry struct {
	root     string
	corePath string
}

// NewMapRuntimeContentStore constructs an empty store.
func NewMapRuntimeContentStore() *MapRuntimeContentStore {
	return &MapRuntimeContentStore{entries: make(map[string]mapRuntimeEntry)}
}

// Add registers the extracted paths for a bundle (as if it were already
// downloaded + extracted).
func (s *MapRuntimeContentStore) Add(bundle NativeRuntimeBundle, extractedRoot, mnnCorePath string) {
	s.entries[nativeBundleKey(bundle)] = mapRuntimeEntry{root: extractedRoot, corePath: mnnCorePath}
}

// Materialise returns the registered paths for bundle, or an error when the
// bundle was not pre-registered.
func (s *MapRuntimeContentStore) Materialise(ctx context.Context, bundle NativeRuntimeBundle, progress func(float64)) (string, string, error) {
	if err := ctx.Err(); err != nil {
		return "", "", err
	}
	e, ok := s.entries[nativeBundleKey(bundle)]
	if !ok {
		return "", "", errors.New("bundle not available in content store")
	}
	if progress != nil {
		progress(0.5)
	}
	return e.root, e.corePath, nil
}

// IsMaterialised reports whether bundle was pre-registered.
func (s *MapRuntimeContentStore) IsMaterialised(ctx context.Context, bundle NativeRuntimeBundle) bool {
	_, ok := s.entries[nativeBundleKey(bundle)]
	return ok
}

func nativeBundleKey(b NativeRuntimeBundle) string {
	return b.MnnVersion + "|" + operatingSystemKindName(b.Os) + "|" + architectureKindName(b.Arch) + "|" + b.Backend.String()
}

// ── enum name / parse helpers (Enum.TryParse / ToString parity) ─────────────

var osKindNames = map[OperatingSystemKind]string{
	OSUnknown: "Unknown", OSWindows: "Windows", OSLinux: "Linux",
	OSMacOS: "MacOS", OSAndroid: "Android", OSIOS: "IOS", OSHarmonyOS: "HarmonyOS",
}

func operatingSystemKindName(os OperatingSystemKind) string {
	if n, ok := osKindNames[os]; ok {
		return n
	}
	return "Unknown"
}

func parseOperatingSystemKind(s string) (OperatingSystemKind, bool) {
	for k, n := range osKindNames {
		if strings.EqualFold(n, strings.TrimSpace(s)) {
			return k, true
		}
	}
	return OSUnknown, false
}

var archKindNames = map[ArchitectureKind]string{
	ArchUnknown: "Unknown", ArchX86: "X86", ArchX64: "X64",
	ArchArm: "Arm", ArchArm64: "Arm64", ArchLoong64: "Loong64",
}

func architectureKindName(arch ArchitectureKind) string {
	if n, ok := archKindNames[arch]; ok {
		return n
	}
	return "Unknown"
}

func parseArchitectureKind(s string) (ArchitectureKind, bool) {
	for k, n := range archKindNames {
		if strings.EqualFold(n, strings.TrimSpace(s)) {
			return k, true
		}
	}
	return ArchUnknown, false
}

// Interface guards.
var (
	_ NativeRuntimeFetcher = (*InMemoryNativeRuntimeFetcher)(nil)
	_ RuntimeContentStore  = (*MapRuntimeContentStore)(nil)
)
