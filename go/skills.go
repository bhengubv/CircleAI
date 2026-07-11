// skills.go
//
// Ports CircleAI.Skills (ISkillStore.cs + SkillSource.cs + SkillSummary.cs +
// SkillDetail.cs + SkillDraft.cs + InMemorySkillStore.cs + SkillPackSource.cs +
// the IPackDownloader seam from SkillPackAutoImporter.cs):
//
//	SkillSource (enum)                         -> int consts (declaration ordinals)
//	SkillSummary / SkillDetail / SkillDraft     -> value structs
//	ISkillStore                                -> SkillStore interface
//	InMemorySkillStore                          -> InMemorySkillStore (+ slug gen)
//	SkillPackSource + KnownSkillPacks           -> value struct + package vars
//	IPackDownloader                            -> PackDownloader interface (+ in-memory impl)
//
// The C# HttpPackDownloader (GitHub tarball fetch + TarFile extract) and
// SkillPackAutoImporter (host-side file walking) are network + filesystem I/O;
// per the port NOTE the download effect is behind the injected PackDownloader
// seam, and a deterministic MapPackDownloader stands in for tests / wiring.
//
// InMemorySkillStore list/search order matches the C# OrderBy(Name,
// OrdinalIgnoreCase); the slug generator reproduces GenerateSlug exactly
// (lowercase, spaces -> "-", strip non [a-z0-9-], collapse "--", trim "-").

package circleai

import (
	"context"
	"errors"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// SkillSource indicates where a skill originated. Ports SkillSource
// (declaration-order ordinals: File=0, InMemory=1, Remote=2).
type SkillSource int

const (
	// SkillSourceFile — loaded from a SKILL.md file on disk.
	SkillSourceFile SkillSource = 0
	// SkillSourceInMemory — created programmatically and held in memory.
	SkillSourceInMemory SkillSource = 1
	// SkillSourceRemote — fetched from a remote skill registry.
	SkillSourceRemote SkillSource = 2
)

// SkillSummary is a lightweight projection of a SkillDetail. Ports the
// SkillSummary record.
type SkillSummary struct {
	ID          string
	Name        string
	Description string
	Tags        []string
	Source      SkillSource
}

// SkillDetail is the full skill record. Ports the SkillDetail record.
type SkillDetail struct {
	ID           string
	Name         string
	Description  string
	Instructions string
	Tags         []string
	Source       SkillSource
	LastModified time.Time
}

// SkillDraft is the input model for creating/updating a skill. Ports the
// SkillDraft record.
type SkillDraft struct {
	Name         string
	Description  string
	Instructions string
	Tags         []string
}

// SkillStore is a persistent store for B! skills. Ports ISkillStore.
type SkillStore interface {
	List(ctx context.Context) ([]SkillSummary, error)
	// Get returns the detail for id and true, or (zero, false) when absent.
	Get(ctx context.Context, id string) (SkillDetail, bool, error)
	Search(ctx context.Context, query string) ([]SkillSummary, error)
	// Upsert creates or replaces a skill; a blank id auto-generates a slug from
	// draft.Name.
	Upsert(ctx context.Context, id string, draft SkillDraft) (SkillDetail, error)
	Delete(ctx context.Context, id string) error
}

// InMemorySkillStore is a thread-safe in-memory SkillStore. Ports
// InMemorySkillStore. Construct with NewInMemorySkillStore.
type InMemorySkillStore struct {
	mu     sync.Mutex
	skills map[string]SkillDetail
}

// NewInMemorySkillStore constructs an empty store.
func NewInMemorySkillStore() *InMemorySkillStore {
	return &InMemorySkillStore{skills: make(map[string]SkillDetail)}
}

// List returns all skills as summaries ordered by Name (OrdinalIgnoreCase).
// Ports ListAsync.
func (s *InMemorySkillStore) List(ctx context.Context) ([]SkillSummary, error) {
	s.mu.Lock()
	out := make([]SkillSummary, 0, len(s.skills))
	for _, d := range s.skills {
		out = append(out, skillToSummary(d))
	}
	s.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return ordinalIgnoreCaseLess(out[i].Name, out[j].Name) })
	return out, nil
}

// Get returns the detail for id. Ports GetAsync. Panics if id is blank (mirrors
// ArgumentException.ThrowIfNullOrWhiteSpace).
func (s *InMemorySkillStore) Get(ctx context.Context, id string) (SkillDetail, bool, error) {
	if strings.TrimSpace(id) == "" {
		panic("id required")
	}
	s.mu.Lock()
	d, ok := s.skills[id]
	s.mu.Unlock()
	return d, ok, nil
}

// Search returns summaries whose Name/Description/Tags contain query
// (case-insensitive), ordered by Name. Ports SearchAsync (empty for blank query).
func (s *InMemorySkillStore) Search(ctx context.Context, query string) ([]SkillSummary, error) {
	if strings.TrimSpace(query) == "" {
		return []SkillSummary{}, nil
	}
	q := strings.ToLower(strings.TrimSpace(query))
	s.mu.Lock()
	out := make([]SkillSummary, 0)
	for _, d := range s.skills {
		if skillMatchesQuery(d, q) {
			out = append(out, skillToSummary(d))
		}
	}
	s.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return ordinalIgnoreCaseLess(out[i].Name, out[j].Name) })
	return out, nil
}

// Upsert creates or replaces a skill (auto-slug on blank id). Ports UpsertAsync.
func (s *InMemorySkillStore) Upsert(ctx context.Context, id string, draft SkillDraft) (SkillDetail, error) {
	effectiveID := strings.TrimSpace(id)
	if effectiveID == "" {
		effectiveID = GenerateSkillSlug(draft.Name)
	}
	tags := draft.Tags
	if tags == nil {
		tags = []string{}
	}
	detail := SkillDetail{
		ID:           effectiveID,
		Name:         draft.Name,
		Description:  draft.Description,
		Instructions: draft.Instructions,
		Tags:         tags,
		Source:       SkillSourceInMemory,
		LastModified: time.Now().UTC(),
	}
	s.mu.Lock()
	s.skills[effectiveID] = detail
	s.mu.Unlock()
	return detail, nil
}

// Delete removes a skill (no-op if absent). Ports DeleteAsync. Panics if id is
// blank.
func (s *InMemorySkillStore) Delete(ctx context.Context, id string) error {
	if strings.TrimSpace(id) == "" {
		panic("id required")
	}
	s.mu.Lock()
	delete(s.skills, id)
	s.mu.Unlock()
	return nil
}

func skillToSummary(d SkillDetail) SkillSummary {
	return SkillSummary{ID: d.ID, Name: d.Name, Description: d.Description, Tags: d.Tags, Source: d.Source}
}

func skillMatchesQuery(d SkillDetail, lowerQuery string) bool {
	if strings.Contains(strings.ToLower(d.Name), lowerQuery) ||
		strings.Contains(strings.ToLower(d.Description), lowerQuery) {
		return true
	}
	for _, t := range d.Tags {
		if strings.Contains(strings.ToLower(t), lowerQuery) {
			return true
		}
	}
	return false
}

var (
	skillSlugSpaces   = regexp.MustCompile(`\s+`)
	skillSlugNonAlnum = regexp.MustCompile(`[^a-z0-9\-]`)
	skillSlugDashes   = regexp.MustCompile(`-{2,}`)
)

// GenerateSkillSlug converts a display name to a URL-safe lowercase slug. Ports
// InMemorySkillStore.GenerateSlug ("My Skill" -> "my-skill"). A name that slugs
// to empty yields a 32-char hex UUID (matching Guid.NewGuid("N")).
func GenerateSkillSlug(name string) string {
	if strings.TrimSpace(name) == "" {
		return strings.ReplaceAll(uuid.NewString(), "-", "")
	}
	slug := strings.ToLower(strings.TrimSpace(name))
	slug = skillSlugSpaces.ReplaceAllString(slug, "-")
	slug = skillSlugNonAlnum.ReplaceAllString(slug, "")
	slug = strings.Trim(skillSlugDashes.ReplaceAllString(slug, "-"), "-")
	if slug == "" {
		return strings.ReplaceAll(uuid.NewString(), "-", "")
	}
	return slug
}

// ── SkillPackSource + KnownSkillPacks ───────────────────────────────────────

// SkillPackSource is a source declaration for a remote skill pack. Ports the
// SkillPackSource record. The C# constructor defaults are applied by the
// NewSkillPackSource helper.
type SkillPackSource struct {
	Name                string
	RepoURL             string
	GitRef              string
	License             string
	SkillSubdir         string
	EstimatedSkillCount int
	IsDefaultEnabled    bool
	DefaultTags         []string
}

// KnownSkillPacks holds the default catalogue of skill packs. Ports
// KnownSkillPacks — each field mirrors the corresponding static readonly.
var (
	// KnownSkillPackAwesomeAgentSkills — bhengubv/awesome-agent-skills.
	KnownSkillPackAwesomeAgentSkills = SkillPackSource{
		Name: "awesome-agent-skills", RepoURL: "https://github.com/bhengubv/awesome-agent-skills",
		GitRef: "main", License: "Apache-2.0", SkillSubdir: "skills",
		EstimatedSkillCount: 1000, IsDefaultEnabled: true, DefaultTags: []string{"community"},
	}
	// KnownSkillPackAnthropicCybersecurity — mukul975/Anthropic-Cybersecurity-Skills.
	KnownSkillPackAnthropicCybersecurity = SkillPackSource{
		Name: "Anthropic-Cybersecurity-Skills", RepoURL: "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
		GitRef: "main", License: "Apache-2.0", SkillSubdir: "skills",
		EstimatedSkillCount: 754, IsDefaultEnabled: true, DefaultTags: []string{"security", "mitre"},
	}
	// KnownSkillPackPrivacyDataProtection — mukul975/Privacy-Data-Protection-Skills.
	KnownSkillPackPrivacyDataProtection = SkillPackSource{
		Name: "Privacy-Data-Protection-Skills", RepoURL: "https://github.com/mukul975/Privacy-Data-Protection-Skills",
		GitRef: "main", License: "Apache-2.0", SkillSubdir: "skills",
		EstimatedSkillCount: 282, IsDefaultEnabled: true, DefaultTags: []string{"privacy", "compliance"},
	}
	// KnownSkillPackClaudeBugHunter — bhengubv/Claude-BugHunter.
	KnownSkillPackClaudeBugHunter = SkillPackSource{
		Name: "Claude-BugHunter", RepoURL: "https://github.com/bhengubv/Claude-BugHunter",
		GitRef: "main", License: "Apache-2.0", SkillSubdir: "skills",
		EstimatedSkillCount: 51, IsDefaultEnabled: true, DefaultTags: []string{"security", "bug-bounty"},
	}
	// KnownSkillPackLast30Days — bhengubv/last30days-skill.
	KnownSkillPackLast30Days = SkillPackSource{
		Name: "last30days-skill", RepoURL: "https://github.com/bhengubv/last30days-skill",
		GitRef: "main", License: "MIT", SkillSubdir: "",
		EstimatedSkillCount: 1, IsDefaultEnabled: true, DefaultTags: []string{"research"},
	}
	// KnownSkillPackEdubaBrand — bhengubv/eduba-brand.
	KnownSkillPackEdubaBrand = SkillPackSource{
		Name: "eduba-brand", RepoURL: "https://github.com/bhengubv/eduba-brand",
		GitRef: "main", License: "n/a (pattern-port)", SkillSubdir: ".agents/skills/eduba-brand",
		EstimatedSkillCount: 1, IsDefaultEnabled: true, DefaultTags: []string{"branding", "eduba"},
	}
	// KnownSkillPackCareerOps — bhengubv/career-ops (disabled by default).
	KnownSkillPackCareerOps = SkillPackSource{
		Name: "career-ops", RepoURL: "https://github.com/bhengubv/career-ops",
		GitRef: "main", License: "MIT", SkillSubdir: "",
		EstimatedSkillCount: 14, IsDefaultEnabled: false, DefaultTags: []string{"job-search", "career", "thejobcenter"},
	}
	// KnownSkillPackBuildYourOwnX — bhengubv/build-your-own-x (disabled by default).
	KnownSkillPackBuildYourOwnX = SkillPackSource{
		Name: "build-your-own-x", RepoURL: "https://github.com/bhengubv/build-your-own-x",
		GitRef: "main", License: "MIT", SkillSubdir: "",
		EstimatedSkillCount: 0, IsDefaultEnabled: false, DefaultTags: []string{"education", "tutorial"},
	}
)

// KnownSkillPacksAll lists every known pack, in the C# All order. Ports
// KnownSkillPacks.All.
func KnownSkillPacksAll() []SkillPackSource {
	return []SkillPackSource{
		KnownSkillPackAwesomeAgentSkills,
		KnownSkillPackAnthropicCybersecurity,
		KnownSkillPackPrivacyDataProtection,
		KnownSkillPackClaudeBugHunter,
		KnownSkillPackLast30Days,
		KnownSkillPackEdubaBrand,
		KnownSkillPackCareerOps,
		KnownSkillPackBuildYourOwnX,
	}
}

// PackDownloader materialises a remote pack into a local directory. Ports
// IPackDownloader — the network effect is injected behind this seam.
type PackDownloader interface {
	// Ensure materialises source under cacheRoot (respecting cacheTTL) and
	// returns the local path containing the extracted repo.
	Ensure(ctx context.Context, source SkillPackSource, cacheRoot string, cacheTTL time.Duration) (string, error)
}

// MapPackDownloader is a deterministic PackDownloader backed by a map from pack
// name -> local path. It is the in-memory stand-in for the real GitHub-tarball
// downloader, letting pack-import flows be wired and exercised without network
// I/O. Construct with NewMapPackDownloader.
type MapPackDownloader struct {
	mu    sync.Mutex
	paths map[string]string
}

// NewMapPackDownloader constructs an empty downloader.
func NewMapPackDownloader() *MapPackDownloader {
	return &MapPackDownloader{paths: make(map[string]string)}
}

// Add registers the local path a pack materialises to (as if already fetched).
func (d *MapPackDownloader) Add(packName, localPath string) {
	d.mu.Lock()
	d.paths[packName] = localPath
	d.mu.Unlock()
}

// Ensure returns the registered local path for source, or an error when the pack
// was not pre-registered. Ports EnsureAsync.
func (d *MapPackDownloader) Ensure(ctx context.Context, source SkillPackSource, cacheRoot string, cacheTTL time.Duration) (string, error) {
	if err := ctx.Err(); err != nil {
		return "", err
	}
	d.mu.Lock()
	p, ok := d.paths[source.Name]
	d.mu.Unlock()
	if !ok {
		return "", errors.New("pack not available in downloader: " + source.Name)
	}
	return p, nil
}

// Interface guards.
var (
	_ SkillStore     = (*InMemorySkillStore)(nil)
	_ PackDownloader = (*MapPackDownloader)(nil)
)
