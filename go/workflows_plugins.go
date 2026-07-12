// workflows_plugins.go
//
// Ports CircleAI.Workflows/PacaPlugins.cs — plugin manifest + lifecycle: reverse
// -DNS name validation, SemVer upgrade detection, marketplace install/upgrade/
// uninstall/enable, per-plugin resource limits. The wazero/WASM execution layer
// is host-supplied via PluginRuntimeHost; this file owns the lifecycle.
//
//	PluginExtensionPoint (enum)  -> int consts + string round-trip
//	PluginManifest / PluginResourceLimits / InstalledPlugin (records) -> structs
//	IPluginRuntimeHost           -> PluginRuntimeHost interface
//	PacaPluginRegistry           -> PacaPluginRegistry
//
// SemVer comparison mirrors the C# (Version.Parse over the pre-release-stripped
// string): numeric dot-separated components, missing trailing components treated
// as 0, compared left-to-right. Reverse-DNS + SemVer validation are exported so
// callers can pre-flight a manifest.

package circleai

import (
	"context"
	"errors"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// PluginExtensionPoint is a marketplace extension point. Ports
// PluginExtensionPoint (declaration order: Sidebar=0 … McpTool=6).
type PluginExtensionPoint int

const (
	// PluginExtensionSidebar — a sidebar surface.
	PluginExtensionSidebar PluginExtensionPoint = 0
	// PluginExtensionTaskDetail — a task-detail surface.
	PluginExtensionTaskDetail PluginExtensionPoint = 1
	// PluginExtensionSettings — a settings surface.
	PluginExtensionSettings PluginExtensionPoint = 2
	// PluginExtensionCustomView — a custom view.
	PluginExtensionCustomView PluginExtensionPoint = 3
	// PluginExtensionRoute — a route.
	PluginExtensionRoute PluginExtensionPoint = 4
	// PluginExtensionEvent — an event hook.
	PluginExtensionEvent PluginExtensionPoint = 5
	// PluginExtensionMcpTool — an MCP tool.
	PluginExtensionMcpTool PluginExtensionPoint = 6
)

// PluginResourceLimits are per-plugin resource limits. Ports the
// PluginResourceLimits record. DefaultPluginResourceLimits mirrors the C#
// defaults (5000ms, 64MB).
type PluginResourceLimits struct {
	CallTimeoutMs      int
	MemoryCeilingBytes int64
}

// DefaultPluginResourceLimits returns the C# default limits (5000ms, 64MB).
func DefaultPluginResourceLimits() PluginResourceLimits {
	return PluginResourceLimits{CallTimeoutMs: 5000, MemoryCeilingBytes: 64 * 1024 * 1024}
}

// PluginManifest is a plugin manifest from plugin.json. Ports the
// PluginManifest record. ArtifactWasmURL / FrontendModuleURL are empty when
// unset (C# nullable Uri?).
type PluginManifest struct {
	Name              string // reverse-DNS, e.g. "com.paca.bdd"
	DisplayName       string
	Version           string // SemVer
	Description       string
	ArtifactWasmURL   string
	FrontendModuleURL string
	ExtensionPoints   []PluginExtensionPoint
	McpTools          []string
	SQLMigrationFiles []string
	Limits            PluginResourceLimits
}

// InstalledPlugin is an installed instance. Ports the InstalledPlugin record.
type InstalledPlugin struct {
	ID                   string // matches Manifest.Name
	Manifest             PluginManifest
	InstalledFromCatalog string
	InstalledAtUTC       time.Time
	Enabled              bool
}

// PluginRuntimeHost is the host-supplied plugin runtime (wazero-style). Ports
// IPluginRuntimeHost.
type PluginRuntimeHost interface {
	// Install installs + initialises: run SQL migrations + cache the WASM artifact.
	Install(ctx context.Context, plugin InstalledPlugin) error
	// Uninstall drops the WASM + cleans artifacts; does NOT roll back data unless asked.
	Uninstall(ctx context.Context, pluginID string, dropArtifacts bool) error
	// Upgrade hot-swaps to a new version (semver upgrade).
	Upgrade(ctx context.Context, from, to InstalledPlugin) error
}

var pluginReverseDNSPattern = regexp.MustCompile(`^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$`)

// PacaPluginRegistry is the plugin lifecycle manager. Ports PacaPluginRegistry.
// Construct with NewPacaPluginRegistry.
type PacaPluginRegistry struct {
	mu        sync.Mutex
	installed map[string]InstalledPlugin
	runtime   PluginRuntimeHost
	clock     func() time.Time
}

// NewPacaPluginRegistry constructs the registry over runtime. clock may be nil.
// Panics if runtime is nil (mirrors the C# ArgumentNullException).
func NewPacaPluginRegistry(runtime PluginRuntimeHost, clock func() time.Time) *PacaPluginRegistry {
	if runtime == nil {
		panic("runtime must not be nil")
	}
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &PacaPluginRegistry{
		installed: make(map[string]InstalledPlugin),
		runtime:   runtime,
		clock:     clock,
	}
}

// ListInstalled returns the installed plugins. Ports ListInstalled.
func (r *PacaPluginRegistry) ListInstalled() []InstalledPlugin {
	r.mu.Lock()
	out := make([]InstalledPlugin, 0, len(r.installed))
	for _, p := range r.installed {
		out = append(out, p)
	}
	r.mu.Unlock()
	return out
}

// Get returns an installed plugin and true, or (zero, false). Ports Get.
func (r *PacaPluginRegistry) Get(id string) (InstalledPlugin, bool) {
	r.mu.Lock()
	p, ok := r.installed[id]
	r.mu.Unlock()
	return p, ok
}

// ValidatePluginManifest validates a manifest before install/upgrade. Ports the
// static ValidateManifest. Returns an error rather than throwing.
func ValidatePluginManifest(manifest PluginManifest) error {
	if !pluginReverseDNSPattern.MatchString(manifest.Name) {
		return errors.New("Plugin name '" + manifest.Name + "' must be reverse-DNS (e.g. com.paca.bdd).")
	}
	if _, ok := parseVersion(stripPrerelease(manifest.Version)); !ok {
		return errors.New("Plugin version '" + manifest.Version + "' is not parseable SemVer.")
	}
	if manifest.Limits.CallTimeoutMs <= 0 {
		return errors.New("CallTimeoutMs must be positive.")
	}
	if manifest.Limits.MemoryCeilingBytes <= 0 {
		return errors.New("MemoryCeilingBytes must be positive.")
	}
	return nil
}

// Install installs a plugin from a manifest. Ports InstallAsync. Returns an
// error on a bad manifest or if the plugin is already installed.
func (r *PacaPluginRegistry) Install(ctx context.Context, manifest PluginManifest, catalog string) (InstalledPlugin, error) {
	if err := ValidatePluginManifest(manifest); err != nil {
		return InstalledPlugin{}, err
	}
	r.mu.Lock()
	if _, exists := r.installed[manifest.Name]; exists {
		r.mu.Unlock()
		return InstalledPlugin{}, errors.New("Plugin '" + manifest.Name + "' is already installed; use Upgrade.")
	}
	r.mu.Unlock()

	installed := InstalledPlugin{
		ID:                   manifest.Name,
		Manifest:             manifest,
		InstalledFromCatalog: catalog,
		InstalledAtUTC:       r.clock(),
		Enabled:              true,
	}
	if err := r.runtime.Install(ctx, installed); err != nil {
		return InstalledPlugin{}, err
	}
	r.mu.Lock()
	r.installed[manifest.Name] = installed
	r.mu.Unlock()
	return installed, nil
}

// Upgrade upgrades if newManifest's SemVer is strictly newer. Ports
// UpgradeAsync. Returns an error if not installed or not newer.
func (r *PacaPluginRegistry) Upgrade(ctx context.Context, newManifest PluginManifest, catalog string) (InstalledPlugin, error) {
	if err := ValidatePluginManifest(newManifest); err != nil {
		return InstalledPlugin{}, err
	}
	r.mu.Lock()
	current, ok := r.installed[newManifest.Name]
	r.mu.Unlock()
	if !ok {
		return InstalledPlugin{}, errors.New("Plugin '" + newManifest.Name + "' is not installed.")
	}
	if CompareSemver(newManifest.Version, current.Manifest.Version) <= 0 {
		return InstalledPlugin{}, errors.New("Version " + newManifest.Version + " is not newer than " + current.Manifest.Version + ".")
	}
	next := InstalledPlugin{
		ID:                   newManifest.Name,
		Manifest:             newManifest,
		InstalledFromCatalog: catalog,
		InstalledAtUTC:       r.clock(),
		Enabled:              current.Enabled,
	}
	if err := r.runtime.Upgrade(ctx, current, next); err != nil {
		return InstalledPlugin{}, err
	}
	r.mu.Lock()
	r.installed[newManifest.Name] = next
	r.mu.Unlock()
	return next, nil
}

// Uninstall removes a plugin and delegates cleanup to the runtime. Ports
// UninstallAsync. No-op if the plugin is not installed.
func (r *PacaPluginRegistry) Uninstall(ctx context.Context, id string, dropArtifacts bool) error {
	r.mu.Lock()
	_, ok := r.installed[id]
	if ok {
		delete(r.installed, id)
	}
	r.mu.Unlock()
	if !ok {
		return nil
	}
	return r.runtime.Uninstall(ctx, id, dropArtifacts)
}

// SetEnabled sets a plugin's enabled flag. Ports SetEnabled. No-op if unknown.
func (r *PacaPluginRegistry) SetEnabled(id string, enabled bool) {
	r.mu.Lock()
	if current, ok := r.installed[id]; ok {
		current.Enabled = enabled
		r.installed[id] = current
	}
	r.mu.Unlock()
}

// CompareSemver compares two SemVer-ish strings, returning <0/0/>0. Ports the
// static CompareSemver.
func CompareSemver(a, b string) int {
	va, _ := parseVersion(stripPrerelease(a))
	vb, _ := parseVersion(stripPrerelease(b))
	for i := 0; i < 4; i++ {
		if va[i] != vb[i] {
			if va[i] < vb[i] {
				return -1
			}
			return 1
		}
	}
	return 0
}

// parseVersion parses a dotted numeric version into 4 components (missing
// trailing components are 0). Mirrors System.Version.Parse: requires 2-4
// numeric parts. Returns ok=false when unparseable.
func parseVersion(v string) ([4]int, bool) {
	var out [4]int
	parts := strings.Split(v, ".")
	if len(parts) < 2 || len(parts) > 4 {
		return out, false
	}
	for i, p := range parts {
		n, err := strconv.Atoi(strings.TrimSpace(p))
		if err != nil || n < 0 {
			return out, false
		}
		out[i] = n
	}
	return out, true
}

// stripPrerelease drops the first '-' or '+' suffix. Ports StripPrerelease
// (v.Split('-', '+')[0]).
func stripPrerelease(v string) string {
	if i := strings.IndexAny(v, "-+"); i >= 0 {
		return v[:i]
	}
	return v
}

// String renders a PluginExtensionPoint as its C# enum name.
func (p PluginExtensionPoint) String() string {
	switch p {
	case PluginExtensionSidebar:
		return "Sidebar"
	case PluginExtensionTaskDetail:
		return "TaskDetail"
	case PluginExtensionSettings:
		return "Settings"
	case PluginExtensionCustomView:
		return "CustomView"
	case PluginExtensionRoute:
		return "Route"
	case PluginExtensionEvent:
		return "Event"
	case PluginExtensionMcpTool:
		return "McpTool"
	default:
		return "PluginExtensionPoint(" + strconv.Itoa(int(p)) + ")"
	}
}
