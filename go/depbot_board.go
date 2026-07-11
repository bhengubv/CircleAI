// depbot_board.go
//
// Ports CircleAI.DepBot (Contracts.cs / InMemoryDepBot.cs / NullImplementations.cs):
//   Dependency / DependencyUpdate
//   IDependencyAnalyzer / IDependencyUpdater
//   FilesystemDependencyAnalyzer / TextRewriteDependencyUpdater
//   NullDependencyAnalyzer / NullDependencyUpdater
//
// The analyzer walks a repo for package.json / requirements.txt / Cargo.toml /
// *.csproj and extracts declared dependencies; the updater rewrites those
// manifests in place. All of this is real, offline, deterministic filesystem
// work and is ported verbatim.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// Dependency is one declared dependency. Ports Dependency. LatestVersion is a
// *string (nil == unknown).
type Dependency struct {
	Ecosystem      string
	Name           string
	CurrentVersion string
	LatestVersion  *string
}

// DependencyUpdate is a proposed version bump. Ports DependencyUpdate.
type DependencyUpdate struct {
	Ecosystem   string
	Name        string
	FromVersion string
	ToVersion   string
	IsBreaking  bool
}

// IDependencyAnalyzer scans a repo for dependencies. Ports IDependencyAnalyzer.
type IDependencyAnalyzer interface {
	BackendID() string
	Scan(ctx context.Context, repoPath string) ([]Dependency, error)
}

// IDependencyUpdater proposes and applies updates. Ports IDependencyUpdater.
type IDependencyUpdater interface {
	BackendID() string
	ProposeUpdates(ctx context.Context, repoPath string) ([]DependencyUpdate, error)
	ApplyUpdate(ctx context.Context, repoPath string, update DependencyUpdate) error
}

// ---------------------------------------------------------------------------
// FilesystemDependencyAnalyzer
// ---------------------------------------------------------------------------

var (
	depReqRx   = regexp.MustCompile(`^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?`)
	depTomlRx  = regexp.MustCompile(`^([A-Za-z0-9_\-]+)\s*=\s*"([^"]+)"`)
	depCsprojRx = regexp.MustCompile(`<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"`)
)

// FilesystemDependencyAnalyzer scans a repo for declared dependencies. Ports
// FilesystemDependencyAnalyzer.
type FilesystemDependencyAnalyzer struct{}

// BackendID returns "filesystem".
func (FilesystemDependencyAnalyzer) BackendID() string { return "filesystem" }

// Scan walks repoPath and extracts dependencies from known manifests. Ports
// ScanAsync.
func (FilesystemDependencyAnalyzer) Scan(ctx context.Context, repoPath string) ([]Dependency, error) {
	if strings.TrimSpace(repoPath) == "" {
		return nil, errors.New("repoPath required")
	}
	info, err := os.Stat(repoPath)
	if err != nil || !info.IsDir() {
		return nil, errors.New("directory not found: " + repoPath)
	}

	results := make([]Dependency, 0)

	err = filepath.WalkDir(repoPath, func(path string, d os.DirEntry, werr error) error {
		if werr != nil || d.IsDir() {
			return nil
		}
		base := d.Name()
		switch {
		case base == "package.json":
			if strings.Contains(path, "node_modules") {
				return nil
			}
			results = append(results, scanPackageJSON(path)...)
		case base == "requirements.txt":
			results = append(results, scanRequirements(path)...)
		case base == "Cargo.toml":
			if strings.Contains(path, "target") {
				return nil
			}
			results = append(results, scanCargoToml(path)...)
		case strings.HasSuffix(base, ".csproj"):
			results = append(results, scanCsproj(path)...)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	return results, nil
}

func scanPackageJSON(path string) []Dependency {
	out := make([]Dependency, 0)
	data, err := os.ReadFile(path)
	if err != nil {
		return out
	}
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil {
		return out
	}
	for _, key := range []string{"dependencies", "devDependencies"} {
		raw, ok := root[key]
		if !ok {
			continue
		}
		var section map[string]string
		if json.Unmarshal(raw, &section) != nil {
			continue
		}
		// Preserve JSON object order by re-reading keys in appearance order.
		for _, name := range jsonObjectKeyOrder(raw) {
			out = append(out, Dependency{Ecosystem: "npm", Name: name, CurrentVersion: section[name], LatestVersion: nil})
		}
	}
	return out
}

// jsonObjectKeyOrder returns the keys of a JSON object in source order.
func jsonObjectKeyOrder(raw json.RawMessage) []string {
	dec := json.NewDecoder(strings.NewReader(string(raw)))
	// Expect opening '{'.
	if t, err := dec.Token(); err != nil {
		return nil
	} else if d, ok := t.(json.Delim); !ok || d != '{' {
		return nil
	}
	keys := make([]string, 0)
	for dec.More() {
		t, err := dec.Token()
		if err != nil {
			break
		}
		key, ok := t.(string)
		if !ok {
			break
		}
		keys = append(keys, key)
		// Skip the value.
		var skip json.RawMessage
		if err := dec.Decode(&skip); err != nil {
			break
		}
	}
	return keys
}

func scanRequirements(path string) []Dependency {
	out := make([]Dependency, 0)
	data, err := os.ReadFile(path)
	if err != nil {
		return out
	}
	for _, rawLine := range strings.Split(strings.ReplaceAll(string(data), "\r\n", "\n"), "\n") {
		line := strings.TrimSpace(rawLine)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		m := depReqRx.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		out = append(out, Dependency{Ecosystem: "pypi", Name: m[1], CurrentVersion: m[3], LatestVersion: nil})
	}
	return out
}

func scanCargoToml(path string) []Dependency {
	out := make([]Dependency, 0)
	data, err := os.ReadFile(path)
	if err != nil {
		return out
	}
	inDeps := false
	for _, rawLine := range strings.Split(strings.ReplaceAll(string(data), "\r\n", "\n"), "\n") {
		line := strings.TrimSpace(rawLine)
		if strings.HasPrefix(line, "[") {
			inDeps = strings.EqualFold(line, "[dependencies]")
			continue
		}
		if !inDeps || line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		m := depTomlRx.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		out = append(out, Dependency{Ecosystem: "cargo", Name: m[1], CurrentVersion: m[2], LatestVersion: nil})
	}
	return out
}

func scanCsproj(path string) []Dependency {
	out := make([]Dependency, 0)
	data, err := os.ReadFile(path)
	if err != nil {
		return out
	}
	for _, m := range depCsprojRx.FindAllStringSubmatch(string(data), -1) {
		out = append(out, Dependency{Ecosystem: "nuget", Name: m[1], CurrentVersion: m[2], LatestVersion: nil})
	}
	return out
}

var _ IDependencyAnalyzer = FilesystemDependencyAnalyzer{}

// ---------------------------------------------------------------------------
// TextRewriteDependencyUpdater
// ---------------------------------------------------------------------------

// TextRewriteDependencyUpdater proposes no fake updates but applies real
// manifest rewrites. Ports TextRewriteDependencyUpdater.
type TextRewriteDependencyUpdater struct{}

// BackendID returns "text-rewrite".
func (TextRewriteDependencyUpdater) BackendID() string { return "text-rewrite" }

// ProposeUpdates returns no updates without a registry (matching the C#
// reference, which does not invent a LatestVersion). Ports ProposeUpdatesAsync.
func (TextRewriteDependencyUpdater) ProposeUpdates(ctx context.Context, repoPath string) ([]DependencyUpdate, error) {
	if strings.TrimSpace(repoPath) == "" {
		return nil, errors.New("repoPath required")
	}
	return []DependencyUpdate{}, nil
}

// ApplyUpdate rewrites the relevant manifests for the given update. Ports
// ApplyUpdateAsync.
func (TextRewriteDependencyUpdater) ApplyUpdate(ctx context.Context, repoPath string, update DependencyUpdate) error {
	if strings.TrimSpace(repoPath) == "" {
		return errors.New("repoPath required")
	}
	info, err := os.Stat(repoPath)
	if err != nil || !info.IsDir() {
		return errors.New("directory not found: " + repoPath)
	}

	switch strings.ToLower(update.Ecosystem) {
	case "nuget":
		pattern := regexp.MustCompile(`<PackageReference\s+Include="` + regexp.QuoteMeta(update.Name) + `"\s+Version="[^"]+"`)
		replacement := `<PackageReference Include="` + update.Name + `" Version="` + update.ToVersion + `"`
		return walkAndRewrite(repoPath, func(p string) bool { return strings.HasSuffix(p, ".csproj") }, func(text string) string {
			return pattern.ReplaceAllString(text, replacement)
		})
	case "npm":
		pattern := regexp.MustCompile(`"` + regexp.QuoteMeta(update.Name) + `"\s*:\s*"[^"]+"`)
		replacement := `"` + update.Name + `": "` + update.ToVersion + `"`
		return walkAndRewrite(repoPath, func(p string) bool {
			return filepath.Base(p) == "package.json" && !strings.Contains(p, "node_modules")
		}, func(text string) string {
			return pattern.ReplaceAllString(text, replacement)
		})
	case "pypi":
		lineRx := regexp.MustCompile(`^` + regexp.QuoteMeta(update.Name) + `\s*[=<>!~]=?\s*[0-9.A-Za-z_\-]+`)
		return walkAndRewriteLines(repoPath, func(p string) bool { return filepath.Base(p) == "requirements.txt" }, func(line string) string {
			trimmed := strings.TrimSpace(line)
			if strings.HasPrefix(trimmed, "#") || trimmed == "" {
				return line
			}
			if lineRx.MatchString(trimmed) {
				return update.Name + "==" + update.ToVersion
			}
			return line
		})
	}
	return nil
}

func walkAndRewrite(root string, match func(string) bool, transform func(string) string) error {
	return filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() || !match(path) {
			return nil
		}
		data, rerr := os.ReadFile(path)
		if rerr != nil {
			return nil
		}
		updated := transform(string(data))
		if updated != string(data) {
			_ = os.WriteFile(path, []byte(updated), 0o644)
		}
		return nil
	})
}

func walkAndRewriteLines(root string, match func(string) bool, transform func(string) string) error {
	return filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() || !match(path) {
			return nil
		}
		data, rerr := os.ReadFile(path)
		if rerr != nil {
			return nil
		}
		// Preserve the original newline style by splitting on "\n".
		hadCRLF := strings.Contains(string(data), "\r\n")
		normal := strings.ReplaceAll(string(data), "\r\n", "\n")
		lines := strings.Split(normal, "\n")
		for i, l := range lines {
			lines[i] = transform(l)
		}
		joined := strings.Join(lines, "\n")
		if hadCRLF {
			joined = strings.ReplaceAll(joined, "\n", "\r\n")
		}
		_ = os.WriteFile(path, []byte(joined), 0o644)
		return nil
	})
}

var _ IDependencyUpdater = TextRewriteDependencyUpdater{}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullDependencyAnalyzer is a fail-safe analyzer. Ports NullDependencyAnalyzer.
type NullDependencyAnalyzer struct{}

// NullDependencyAnalyzerInstance is the shared singleton.
var NullDependencyAnalyzerInstance = NullDependencyAnalyzer{}

func (NullDependencyAnalyzer) BackendID() string { return "null" }
func (NullDependencyAnalyzer) Scan(context.Context, string) ([]Dependency, error) {
	return []Dependency{}, nil
}

// NullDependencyUpdater is a fail-safe updater. Ports NullDependencyUpdater.
type NullDependencyUpdater struct{}

// NullDependencyUpdaterInstance is the shared singleton.
var NullDependencyUpdaterInstance = NullDependencyUpdater{}

func (NullDependencyUpdater) BackendID() string { return "null" }
func (NullDependencyUpdater) ProposeUpdates(context.Context, string) ([]DependencyUpdate, error) {
	return []DependencyUpdate{}, nil
}
func (NullDependencyUpdater) ApplyUpdate(context.Context, string, DependencyUpdate) error {
	return nil
}

var (
	_ IDependencyAnalyzer = NullDependencyAnalyzer{}
	_ IDependencyUpdater  = NullDependencyUpdater{}
)
