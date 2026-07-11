// distribution.go
//
// Ports CircleAI.Distribution core contracts (Contracts.cs +
// NullImplementations.cs) plus the DISTRIBUTION section of the Ubiquity rails
// (UbiquityRails.cs + UbiquityRailsMissingDefaults.cs) named in the work unit:
//
//	FileMetadata / Peer (records)          -> value structs
//	IFileSync / IPeerAdvertiser             -> FileSync / PeerAdvertiser
//	NullFileSync / NullPeerAdvertiser       -> null impls
//	AppStorePackage / IAppStoreSubmitter    -> AppStorePackage / AppStoreSubmitter
//	DefaultAppStoreSubmitter                -> DefaultAppStoreSubmitter (real)
//	DeltaUpdate / ISignedDeltaUpdater       -> DeltaUpdate / SignedDeltaUpdater
//	DefaultSignedDeltaUpdater               -> DefaultSignedDeltaUpdater (HMAC-gated)
//	IOemPreloadCatalog / ICarrierPreloadCatalog + defaults
//	IPwaFallback / ISideloadChannel / ILinuxRepoFanout + defaults
//
// The remaining Ubiquity rails (onboarding, trust, pricing, localisation,
// hardware, services, regulator, recovery, failure-mode, cost, network-effect,
// cultural) are ported in distribution_ubiquity.go.

package circleai

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"errors"
	"strings"
	"sync"
)

// FileMetadata describes a content-addressed file. Ports the FileMetadata record.
type FileMetadata struct {
	ContentHash string
	Name        string
	SizeBytes   int64
}

// Peer is a discovered sync peer. Ports the Peer record.
type Peer struct {
	PeerID          string
	Endpoint        string
	AvailableHashes []string
}

// FileSync is a content-addressed file sync surface. Ports IFileSync.
type FileSync interface {
	BackendID() string
	Has(ctx context.Context, contentHash string) (bool, error)
	// Fetch returns the payload and true, or (nil, false) when absent.
	Fetch(ctx context.Context, contentHash string) ([]byte, bool, error)
	Announce(ctx context.Context, metadata FileMetadata, payload []byte) error
}

// PeerAdvertiser discovers sync peers. Ports IPeerAdvertiser.
type PeerAdvertiser interface {
	BackendID() string
	Discover(ctx context.Context) ([]Peer, error)
}

// NullFileSync is a no-op file sync. Ports NullFileSync.
type NullFileSync struct{}

// NullFileSyncInstance mirrors NullFileSync.Instance.
var NullFileSyncInstance = NullFileSync{}

// BackendID returns "null".
func (NullFileSync) BackendID() string                              { return "null" }
func (NullFileSync) Has(context.Context, string) (bool, error)      { return false, nil }
func (NullFileSync) Fetch(context.Context, string) ([]byte, bool, error) { return nil, false, nil }
func (NullFileSync) Announce(context.Context, FileMetadata, []byte) error { return nil }

// NullPeerAdvertiser is a no-op peer advertiser. Ports NullPeerAdvertiser.
type NullPeerAdvertiser struct{}

// NullPeerAdvertiserInstance mirrors NullPeerAdvertiser.Instance.
var NullPeerAdvertiserInstance = NullPeerAdvertiser{}

// BackendID returns "null".
func (NullPeerAdvertiser) BackendID() string { return "null" }
func (NullPeerAdvertiser) Discover(context.Context) ([]Peer, error) {
	return []Peer{}, nil
}

// ── DISTRIBUTION rails ──────────────────────────────────────────────────────

// AppStorePackage is a package submitted to an app store. Ports the
// AppStorePackage record.
type AppStorePackage struct {
	StoreName   string
	PackagePath string
	Version     string
	Metadata    map[string]string
}

// AppStoreSubmitter submits packages to app stores. Ports IAppStoreSubmitter.
type AppStoreSubmitter interface {
	Submit(ctx context.Context, pkg AppStorePackage) (bool, error)
}

// DefaultAppStoreSubmitter validates the package and records the submission.
// Ports DefaultAppStoreSubmitter. The zero value is not usable — construct with
// NewDefaultAppStoreSubmitter.
type DefaultAppStoreSubmitter struct {
	mu        sync.Mutex
	submitted map[string]AppStorePackage
	order     []string
}

var appStoreKnownStores = map[string]bool{
	"playstore": true, "appstore": true, "galaxy store": true,
	"huawei appgallery": true, "microsoft store": true, "f-droid": true,
}

// NewDefaultAppStoreSubmitter constructs an empty submitter.
func NewDefaultAppStoreSubmitter() *DefaultAppStoreSubmitter {
	return &DefaultAppStoreSubmitter{submitted: make(map[string]AppStorePackage)}
}

// Submit validates and records the package, returning false for an unknown
// store. Ports SubmitAsync. Returns an error when a required field is blank
// (mirrors the C# ArgumentException).
func (s *DefaultAppStoreSubmitter) Submit(ctx context.Context, pkg AppStorePackage) (bool, error) {
	if strings.TrimSpace(pkg.StoreName) == "" {
		return false, errors.New("StoreName required")
	}
	if strings.TrimSpace(pkg.PackagePath) == "" {
		return false, errors.New("PackagePath required")
	}
	if strings.TrimSpace(pkg.Version) == "" {
		return false, errors.New("Version required")
	}
	if !appStoreKnownStores[strings.ToLower(pkg.StoreName)] {
		return false, nil
	}
	key := pkg.StoreName + "/" + pkg.Version
	s.mu.Lock()
	if _, exists := s.submitted[key]; !exists {
		s.order = append(s.order, key)
	}
	s.submitted[key] = pkg
	s.mu.Unlock()
	return true, nil
}

// Submitted returns the recorded submissions (submission order). Ports the
// Submitted property.
func (s *DefaultAppStoreSubmitter) Submitted() []AppStorePackage {
	s.mu.Lock()
	out := make([]AppStorePackage, 0, len(s.order))
	for _, k := range s.order {
		out = append(out, s.submitted[k])
	}
	s.mu.Unlock()
	return out
}

// DeltaUpdate is a signed delta update. Ports the DeltaUpdate record.
type DeltaUpdate struct {
	Channel     string
	FromVersion string
	ToVersion   string
	Payload     []byte
	Signature   []byte
}

// SignedDeltaUpdater applies signed delta updates. Ports ISignedDeltaUpdater.
type SignedDeltaUpdater interface {
	Apply(ctx context.Context, update DeltaUpdate) (bool, error)
}

// DefaultSignedDeltaUpdater verifies an HMAC-SHA256 signature before applying.
// Ports DefaultSignedDeltaUpdater. Construct with NewDefaultSignedDeltaUpdater.
type DefaultSignedDeltaUpdater struct {
	hmacKey        []byte
	mu             sync.Mutex
	channelVersion map[string]string
}

// NewDefaultSignedDeltaUpdater constructs the updater with an HMAC key of at
// least 16 bytes. Panics on a short key (mirrors ArgumentException).
func NewDefaultSignedDeltaUpdater(hmacKey []byte) *DefaultSignedDeltaUpdater {
	if len(hmacKey) < 16 {
		panic("hmacKey must be at least 16 bytes")
	}
	return &DefaultSignedDeltaUpdater{hmacKey: hmacKey, channelVersion: make(map[string]string)}
}

// Apply verifies the signature and version chain, then records the new version.
// Ports ApplyAsync. Returns false (not an error) for a blank channel/toVersion,
// a version-chain mismatch, or a signature mismatch.
func (u *DefaultSignedDeltaUpdater) Apply(ctx context.Context, update DeltaUpdate) (bool, error) {
	if strings.TrimSpace(update.Channel) == "" || strings.TrimSpace(update.ToVersion) == "" {
		return false, nil
	}
	u.mu.Lock()
	current, ok := u.channelVersion[update.Channel]
	u.mu.Unlock()
	if ok && current != update.FromVersion {
		return false, nil
	}
	mac := hmac.New(sha256.New, u.hmacKey)
	mac.Write([]byte(update.Channel + "|" + update.FromVersion + "|" + update.ToVersion + "|"))
	mac.Write(update.Payload)
	expected := mac.Sum(nil)
	if !hmac.Equal(expected, update.Signature) {
		return false, nil
	}
	u.mu.Lock()
	u.channelVersion[update.Channel] = update.ToVersion
	u.mu.Unlock()
	return true, nil
}

// CurrentVersion returns the recorded version for a channel and true, or
// ("", false). Ports CurrentVersion.
func (u *DefaultSignedDeltaUpdater) CurrentVersion(channel string) (string, bool) {
	u.mu.Lock()
	v, ok := u.channelVersion[channel]
	u.mu.Unlock()
	return v, ok
}

// DeltaUpdateSignature computes the HMAC-SHA256 signature
// DefaultSignedDeltaUpdater expects (Channel|FromVersion|ToVersion|Payload), for
// callers/tests that need to produce a valid update.
func DeltaUpdateSignature(hmacKey []byte, update DeltaUpdate) []byte {
	mac := hmac.New(sha256.New, hmacKey)
	mac.Write([]byte(update.Channel + "|" + update.FromVersion + "|" + update.ToVersion + "|"))
	mac.Write(update.Payload)
	return mac.Sum(nil)
}

// OemPreloadCatalog lists OEM preload partners. Ports IOemPreloadCatalog.
type OemPreloadCatalog interface{ Partners() []string }

// DefaultOemPreloadCatalog is the default OEM partner list. Ports
// DefaultOemPreloadCatalog.
type DefaultOemPreloadCatalog struct{}

// Partners returns the default OEM partners. Ports the Partners property.
func (DefaultOemPreloadCatalog) Partners() []string {
	return []string{"Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"}
}

// CarrierPreloadCatalog lists carrier preload partners. Ports ICarrierPreloadCatalog.
type CarrierPreloadCatalog interface{ Carriers() []string }

// DefaultCarrierPreloadCatalog is the default carrier list. Ports
// DefaultCarrierPreloadCatalog.
type DefaultCarrierPreloadCatalog struct{}

// Carriers returns the default carriers. Ports the Carriers property.
func (DefaultCarrierPreloadCatalog) Carriers() []string {
	return []string{"MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"}
}

// PwaFallback exposes the PWA fallback URL. Ports IPwaFallback.
type PwaFallback interface{ PwaURL() string }

// DefaultPwaFallback is the default PWA fallback. Ports DefaultPwaFallback.
type DefaultPwaFallback struct{}

// PwaURL returns the default PWA URL. Ports the PwaUrl property.
func (DefaultPwaFallback) PwaURL() string { return "https://app.circle.ai" }

// SideloadChannel lists sideload formats. Ports ISideloadChannel.
type SideloadChannel interface{ Formats() []string }

// DefaultSideloadChannel is the default sideload channel. Ports
// DefaultSideloadChannel.
type DefaultSideloadChannel struct{}

// Formats returns the default sideload formats. Ports the Formats property.
func (DefaultSideloadChannel) Formats() []string { return []string{"APK", "IPA", "MSIX"} }

// LinuxRepoFanout lists Linux package repos. Ports ILinuxRepoFanout.
type LinuxRepoFanout interface{ Repos() []string }

// DefaultLinuxRepoFanout is the default Linux repo fanout. Ports
// DefaultLinuxRepoFanout.
type DefaultLinuxRepoFanout struct{}

// Repos returns the default Linux repos. Ports the Repos property.
func (DefaultLinuxRepoFanout) Repos() []string {
	return []string{"apt", "yum", "pacman", "brew", "flatpak", "snap"}
}

// Interface guards.
var (
	_ FileSync              = NullFileSync{}
	_ PeerAdvertiser        = NullPeerAdvertiser{}
	_ AppStoreSubmitter     = (*DefaultAppStoreSubmitter)(nil)
	_ SignedDeltaUpdater    = (*DefaultSignedDeltaUpdater)(nil)
	_ OemPreloadCatalog     = DefaultOemPreloadCatalog{}
	_ CarrierPreloadCatalog = DefaultCarrierPreloadCatalog{}
	_ PwaFallback           = DefaultPwaFallback{}
	_ SideloadChannel       = DefaultSideloadChannel{}
	_ LinuxRepoFanout       = DefaultLinuxRepoFanout{}
)
