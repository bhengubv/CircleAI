// code_understanding.go
//
// Ports CircleAI.CodeUnderstanding (Contracts.cs / InMemoryCodeUnderstanding.cs
// / NullImplementations.cs):
//   CodeSymbol / CodeMatch / SymbolEdge
//   ICodeIndexer / ICodeSearch / ISymbolGraph
//   FilesystemCodeIndexer / IndexBackedCodeSearch / InMemorySymbolGraph
//   NullCodeIndexer / NullCodeSearch / NullSymbolGraph
//
// The indexer walks a repo and pulls declarations from .cs/.ts/.js/.py/.go via
// a per-language regex pass. Go's RE2 has no lookbehind, so the C# (?<=...)
// declaration patterns are rewritten to capture the keyword + the identifier
// (the identifier is the second capture group, matching the C# m.Groups[2]).
// The obj/bin/node_modules skip, the extension filter, and the substring
// search + host-populated symbol graph are ported verbatim.

package circleai

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
)

// CodeSymbol is a declared symbol. Ports CodeSymbol.
type CodeSymbol struct {
	Path string
	Line int
	Name string
	Kind string
}

// CodeMatch is a search hit. Ports CodeMatch.
type CodeMatch struct {
	Path    string
	Line    int
	Snippet string
	Score   float32
}

// SymbolEdge is a directed symbol relation. Ports SymbolEdge.
type SymbolEdge struct {
	From CodeSymbol
	To   CodeSymbol
	Kind string
}

// ICodeIndexer indexes a repo's symbols. Ports ICodeIndexer.
type ICodeIndexer interface {
	BackendID() string
	Index(ctx context.Context, repoPath string) error
	CountSymbols(ctx context.Context, repoPath string) (int, error)
}

// ICodeSearch searches indexed code. Ports ICodeSearch.
type ICodeSearch interface {
	BackendID() string
	Search(ctx context.Context, query string, topK int) ([]CodeMatch, error)
	SemanticSearch(ctx context.Context, query string, topK int) ([]CodeMatch, error)
}

// ISymbolGraph resolves symbol callers/callees. Ports ISymbolGraph.
type ISymbolGraph interface {
	BackendID() string
	CallersOf(ctx context.Context, s CodeSymbol) ([]SymbolEdge, error)
	CalleesOf(ctx context.Context, s CodeSymbol) ([]SymbolEdge, error)
}

// ---------------------------------------------------------------------------
// FilesystemCodeIndexer
// ---------------------------------------------------------------------------

type codeLanguage struct {
	ext  string
	rx   *regexp.Regexp
	kind string
	// idGroup is the capture-group index holding the identifier (mirrors the
	// C# m.Groups[2]).
	idGroup int
}

// codeLanguages mirrors the C# Languages table, rewritten for RE2 (no
// lookbehind). Each pattern captures the keyword in group 1 and the identifier
// in group 2.
var codeLanguages = []codeLanguage{
	{".cs", regexp.MustCompile(`\b(class|interface|record|enum|struct)\s+(\w+)`), "csharp", 2},
	{".cs", regexp.MustCompile(`\b(public|private|internal|protected|static)\s+\w+\s+(\w+)\s*\(`), "csharp-method", 2},
	{".ts", regexp.MustCompile(`\b(class|interface|type|enum)\s+(\w+)`), "ts", 2},
	{".js", regexp.MustCompile(`\b(class|function)\s+(\w+)`), "js", 2},
	{".py", regexp.MustCompile(`(?m)^\s*(def|class)\s+(\w+)`), "python", 2},
	{".go", regexp.MustCompile(`(?m)^\s*func\s+(\(\w+\s+\*?\w+\)\s+)?(\w+)`), "go", 2},
}

// FilesystemCodeIndexer indexes declarations via a regex pass. Ports
// FilesystemCodeIndexer.
type FilesystemCodeIndexer struct {
	mu    sync.Mutex
	index map[string][]CodeSymbol
}

// NewFilesystemCodeIndexer constructs an empty indexer.
func NewFilesystemCodeIndexer() *FilesystemCodeIndexer {
	return &FilesystemCodeIndexer{index: make(map[string][]CodeSymbol)}
}

// BackendID returns "filesystem".
func (ix *FilesystemCodeIndexer) BackendID() string { return "filesystem" }

// Index walks repoPath and records declarations. Ports IndexAsync.
func (ix *FilesystemCodeIndexer) Index(ctx context.Context, repoPath string) error {
	if strings.TrimSpace(repoPath) == "" {
		return errors.New("repoPath required")
	}
	info, err := os.Stat(repoPath)
	if err != nil || !info.IsDir() {
		return errors.New("directory not found: " + repoPath)
	}

	symbols := make([]CodeSymbol, 0)
	files, err := enumerateSourceFiles(repoPath)
	if err != nil {
		return err
	}
	for _, path := range files {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		data, rerr := os.ReadFile(path)
		if rerr != nil {
			continue
		}
		lines := strings.Split(strings.ReplaceAll(string(data), "\r\n", "\n"), "\n")
		ext := strings.ToLower(filepath.Ext(path))
		for i, line := range lines {
			for _, lang := range codeLanguages {
				if lang.ext != ext {
					continue
				}
				for _, m := range lang.rx.FindAllStringSubmatch(line, -1) {
					if len(m) > lang.idGroup && m[lang.idGroup] != "" {
						symbols = append(symbols, CodeSymbol{Path: path, Line: i + 1, Name: m[lang.idGroup], Kind: lang.kind})
					}
				}
			}
		}
	}
	ix.mu.Lock()
	ix.index[repoPath] = symbols
	ix.mu.Unlock()
	return nil
}

// CountSymbols returns the symbol count for repoPath. Ports CountSymbolsAsync.
func (ix *FilesystemCodeIndexer) CountSymbols(ctx context.Context, repoPath string) (int, error) {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	if l, ok := ix.index[repoPath]; ok {
		return len(l), nil
	}
	return 0, nil
}

// allSymbols returns a flat snapshot of every indexed symbol.
func (ix *FilesystemCodeIndexer) allSymbols() []CodeSymbol {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	out := make([]CodeSymbol, 0)
	for _, l := range ix.index {
		out = append(out, l...)
	}
	return out
}

func enumerateSourceFiles(root string) ([]string, error) {
	sep := string(os.PathSeparator)
	out := make([]string, 0)
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return nil // skip unreadable entries
		}
		if d.IsDir() {
			return nil
		}
		ext := strings.ToLower(filepath.Ext(path))
		switch ext {
		case ".cs", ".ts", ".js", ".py", ".go":
		default:
			return nil
		}
		if strings.Contains(path, sep+"obj"+sep) || strings.Contains(path, sep+"bin"+sep) || strings.Contains(path, sep+"node_modules"+sep) {
			return nil
		}
		out = append(out, path)
		return nil
	})
	return out, err
}

var _ ICodeIndexer = (*FilesystemCodeIndexer)(nil)

// ---------------------------------------------------------------------------
// IndexBackedCodeSearch
// ---------------------------------------------------------------------------

// IndexBackedCodeSearch searches symbols by substring. Ports
// IndexBackedCodeSearch.
type IndexBackedCodeSearch struct {
	indexer *FilesystemCodeIndexer
}

// NewIndexBackedCodeSearch constructs a search over an indexer. Panics if
// indexer is nil.
func NewIndexBackedCodeSearch(indexer *FilesystemCodeIndexer) *IndexBackedCodeSearch {
	if indexer == nil {
		panic("indexer must not be nil")
	}
	return &IndexBackedCodeSearch{indexer: indexer}
}

// BackendID returns "index-backed".
func (s *IndexBackedCodeSearch) BackendID() string { return "index-backed" }

// Search returns up to topK symbols whose name contains query. Ports
// SearchAsync.
func (s *IndexBackedCodeSearch) Search(ctx context.Context, query string, topK int) ([]CodeMatch, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	hits := make([]CodeMatch, 0)
	for _, sym := range s.indexer.allSymbols() {
		if strings.Contains(strings.ToLower(sym.Name), strings.ToLower(query)) {
			hits = append(hits, CodeMatch{Path: sym.Path, Line: sym.Line, Snippet: sym.Kind + " " + sym.Name, Score: 1.0})
			if len(hits) == topK {
				break
			}
		}
	}
	return hits, nil
}

// SemanticSearch falls back to substring Search. Ports SemanticSearchAsync.
func (s *IndexBackedCodeSearch) SemanticSearch(ctx context.Context, query string, topK int) ([]CodeMatch, error) {
	return s.Search(ctx, query, topK)
}

var _ ICodeSearch = (*IndexBackedCodeSearch)(nil)

// ---------------------------------------------------------------------------
// InMemorySymbolGraph
// ---------------------------------------------------------------------------

// InMemorySymbolGraph is a host-populated symbol adjacency list. Ports
// InMemorySymbolGraph.
type InMemorySymbolGraph struct {
	mu    sync.Mutex
	edges []SymbolEdge
}

// NewInMemorySymbolGraph constructs an empty graph.
func NewInMemorySymbolGraph() *InMemorySymbolGraph {
	return &InMemorySymbolGraph{}
}

// BackendID returns "in-memory".
func (g *InMemorySymbolGraph) BackendID() string { return "in-memory" }

// Link records a from→to edge (default kind "calls"). Ports Link.
func (g *InMemorySymbolGraph) Link(from, to CodeSymbol, kind string) {
	if kind == "" {
		kind = "calls"
	}
	g.mu.Lock()
	g.edges = append(g.edges, SymbolEdge{From: from, To: to, Kind: kind})
	g.mu.Unlock()
}

// CallersOf returns edges whose target name equals s.Name. Ports CallersOfAsync.
func (g *InMemorySymbolGraph) CallersOf(ctx context.Context, s CodeSymbol) ([]SymbolEdge, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make([]SymbolEdge, 0)
	for _, e := range g.edges {
		if e.To.Name == s.Name {
			out = append(out, e)
		}
	}
	return out, nil
}

// CalleesOf returns edges whose source name equals s.Name. Ports CalleesOfAsync.
func (g *InMemorySymbolGraph) CalleesOf(ctx context.Context, s CodeSymbol) ([]SymbolEdge, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make([]SymbolEdge, 0)
	for _, e := range g.edges {
		if e.From.Name == s.Name {
			out = append(out, e)
		}
	}
	return out, nil
}

var _ ISymbolGraph = (*InMemorySymbolGraph)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullCodeIndexer is a fail-safe indexer. Ports NullCodeIndexer.
type NullCodeIndexer struct{}

// NullCodeIndexerInstance is the shared singleton.
var NullCodeIndexerInstance = NullCodeIndexer{}

func (NullCodeIndexer) BackendID() string                   { return "null" }
func (NullCodeIndexer) Index(context.Context, string) error { return nil }
func (NullCodeIndexer) CountSymbols(context.Context, string) (int, error) {
	return 0, nil
}

// NullCodeSearch is a fail-safe search. Ports NullCodeSearch.
type NullCodeSearch struct{}

// NullCodeSearchInstance is the shared singleton.
var NullCodeSearchInstance = NullCodeSearch{}

func (NullCodeSearch) BackendID() string { return "null" }
func (NullCodeSearch) Search(context.Context, string, int) ([]CodeMatch, error) {
	return []CodeMatch{}, nil
}
func (NullCodeSearch) SemanticSearch(context.Context, string, int) ([]CodeMatch, error) {
	return []CodeMatch{}, nil
}

// NullSymbolGraph is a fail-safe graph. Ports NullSymbolGraph.
type NullSymbolGraph struct{}

// NullSymbolGraphInstance is the shared singleton.
var NullSymbolGraphInstance = NullSymbolGraph{}

func (NullSymbolGraph) BackendID() string { return "null" }
func (NullSymbolGraph) CallersOf(context.Context, CodeSymbol) ([]SymbolEdge, error) {
	return []SymbolEdge{}, nil
}
func (NullSymbolGraph) CalleesOf(context.Context, CodeSymbol) ([]SymbolEdge, error) {
	return []SymbolEdge{}, nil
}

var (
	_ ICodeIndexer = NullCodeIndexer{}
	_ ICodeSearch  = NullCodeSearch{}
	_ ISymbolGraph = NullSymbolGraph{}
)
