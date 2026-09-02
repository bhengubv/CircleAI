// devtools_board.go
//
// Ports CircleAI.DevTools (Contracts.cs / InMemoryDevTools.cs /
// NullImplementations.cs):
//   FileEdit / InlineSuggestion / AgentTurn / PatchPlan / RefactorRequest
//   ICodeEditor / IInlineSuggester / IAgentShell / IPatchPlanner / IRefactorTool
//   FilesystemCodeEditor / TokenContextInlineSuggester / InMemoryAgentShell /
//     PatternMatchPatchPlanner / RegexRefactorTool
//   NullCodeEditor / NullInlineSuggester / NullAgentShell / NullPatchPlanner /
//     NullRefactorTool
//
// The real work — offset-range edits, next-token prediction from a file's own
// identifier vocabulary, "rename X to Y" / "remove line N" / "append" goal
// parsing, and Rename + ExtractConstant refactors — is ported verbatim. Edit
// ranges are byte offsets (C# uses UTF-16 char offsets; the two agree for the
// ASCII source these tools operate on).

package circleai

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"

	"github.com/google/uuid"
)

// FileEdit is a range replacement in a file. Ports FileEdit.
type FileEdit struct {
	Path        string
	RangeStart  int
	RangeEnd    int
	Replacement string
}

// InlineSuggestion is a ghost-text suggestion. Ports InlineSuggestion.
type InlineSuggestion struct {
	Text       string
	Confidence float32
}

// AgentTurn is one agent-shell turn. Ports AgentTurn.
type AgentTurn struct {
	TurnID     string
	UserPrompt string
	Response   string
	Edits      []FileEdit
}

// PatchPlan is a proposed multi-file patch. Ports PatchPlan.
type PatchPlan struct {
	Goal          string
	Steps         []string
	ProposedEdits []FileEdit
}

// RefactorRequest is a cross-file refactor request. Ports RefactorRequest.
type RefactorRequest struct {
	Description string
	TargetPaths []string
}

// ICodeEditor reads/writes editor buffers. Ports ICodeEditor.
type ICodeEditor interface {
	BackendID() string
	Read(ctx context.Context, path string) (string, error)
	Apply(ctx context.Context, edits []FileEdit) error
	Save(ctx context.Context, path string) error
}

// IInlineSuggester is a tab-completion suggester. Ports IInlineSuggester.
type IInlineSuggester interface {
	BackendID() string
	// Suggest returns a suggestion, or (zero,false) when none applies.
	Suggest(ctx context.Context, path string, line, column int, contextBefore string) (InlineSuggestion, bool, error)
}

// IAgentShell is an agent-shell loop. Ports IAgentShell.
type IAgentShell interface {
	BackendID() string
	RunTurn(ctx context.Context, userPrompt string) (AgentTurn, error)
	History(ctx context.Context, limit int) ([]AgentTurn, error)
}

// IPatchPlanner proposes then applies a patch plan. Ports IPatchPlanner.
type IPatchPlanner interface {
	BackendID() string
	Plan(ctx context.Context, goal string) (PatchPlan, error)
	Apply(ctx context.Context, plan PatchPlan) error
}

// IRefactorTool proposes cross-file edits. Ports IRefactorTool.
type IRefactorTool interface {
	BackendID() string
	Propose(ctx context.Context, request RefactorRequest) ([]FileEdit, error)
}

// ---------------------------------------------------------------------------
// FilesystemCodeEditor
// ---------------------------------------------------------------------------

// FilesystemCodeEditor reads/writes files on disk. Ports FilesystemCodeEditor.
type FilesystemCodeEditor struct{}

// BackendID returns "filesystem".
func (FilesystemCodeEditor) BackendID() string { return "filesystem" }

// Read returns the file contents. Ports ReadAsync.
func (FilesystemCodeEditor) Read(ctx context.Context, path string) (string, error) {
	if strings.TrimSpace(path) == "" {
		return "", errors.New("path required")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// Apply groups edits per file and applies them highest-offset-first so earlier
// offsets stay valid. Ports ApplyAsync.
func (FilesystemCodeEditor) Apply(ctx context.Context, edits []FileEdit) error {
	byFile := make(map[string][]FileEdit)
	order := make([]string, 0)
	for _, e := range edits {
		if _, ok := byFile[e.Path]; !ok {
			order = append(order, e.Path)
		}
		byFile[e.Path] = append(byFile[e.Path], e)
	}
	for _, path := range order {
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		text := []byte(data)
		fileEdits := append([]FileEdit(nil), byFile[path]...)
		sort.SliceStable(fileEdits, func(i, j int) bool { return fileEdits[i].RangeStart > fileEdits[j].RangeStart })
		for _, e := range fileEdits {
			if e.RangeStart < 0 || e.RangeEnd > len(text) || e.RangeEnd < e.RangeStart {
				return errors.New("invalid edit range " + strconv.Itoa(e.RangeStart) + ".." + strconv.Itoa(e.RangeEnd) + " for " + e.Path)
			}
			out := make([]byte, 0, len(text)-(e.RangeEnd-e.RangeStart)+len(e.Replacement))
			out = append(out, text[:e.RangeStart]...)
			out = append(out, e.Replacement...)
			out = append(out, text[e.RangeEnd:]...)
			text = out
		}
		if err := os.WriteFile(path, text, 0o644); err != nil {
			return err
		}
	}
	return nil
}

// Save is a no-op (writes happen in Apply). Ports SaveAsync.
func (FilesystemCodeEditor) Save(context.Context, string) error { return nil }

var _ ICodeEditor = FilesystemCodeEditor{}

// ---------------------------------------------------------------------------
// TokenContextInlineSuggester
// ---------------------------------------------------------------------------

var devToolsIdentifierRx = regexp.MustCompile(`[A-Za-z_][A-Za-z0-9_]*`)

// TokenContextInlineSuggester predicts the next token from the file's own
// identifier vocabulary. Ports TokenContextInlineSuggester.
type TokenContextInlineSuggester struct{}

// BackendID returns "token-context".
func (TokenContextInlineSuggester) BackendID() string { return "token-context" }

// Suggest completes the partial identifier at the cursor with the most frequent
// matching identifier in the file. Ports SuggestAsync.
func (TokenContextInlineSuggester) Suggest(ctx context.Context, path string, line, column int, contextBefore string) (InlineSuggestion, bool, error) {
	if strings.TrimSpace(path) == "" {
		return InlineSuggestion{}, false, errors.New("path required")
	}
	partial := extractPartialAtCursor(contextBefore)
	if len(partial) < 2 {
		return InlineSuggestion{}, false, nil
	}
	fileText := contextBefore
	if data, err := os.ReadFile(path); err == nil {
		fileText = string(data)
	}
	freq := make(map[string]int)
	for _, m := range devToolsIdentifierRx.FindAllString(fileText, -1) {
		if strings.HasPrefix(m, partial) && len(m) > len(partial) {
			freq[m]++
		}
	}
	if len(freq) == 0 {
		return InlineSuggestion{}, false, nil
	}
	// Highest frequency, ties broken by shortest identifier.
	bestKey := ""
	bestCount := -1
	keys := make([]string, 0, len(freq))
	for k := range freq {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		c := freq[k]
		if c > bestCount || (c == bestCount && len(k) < len(bestKey)) {
			bestKey = k
			bestCount = c
		}
	}
	completion := bestKey[len(partial):]
	confidence := float32(bestCount) / 10.0
	if confidence > 1.0 {
		confidence = 1.0
	}
	return InlineSuggestion{Text: completion, Confidence: confidence}, true, nil
}

func extractPartialAtCursor(contextBefore string) string {
	i := len(contextBefore)
	for i > 0 {
		ch := contextBefore[i-1]
		if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' {
			i--
			continue
		}
		break
	}
	return contextBefore[i:]
}

var _ IInlineSuggester = TokenContextInlineSuggester{}

// ---------------------------------------------------------------------------
// InMemoryAgentShell
// ---------------------------------------------------------------------------

// AgentExecutor executes one prompt into a turn. Ports the executor delegate.
type AgentExecutor func(ctx context.Context, prompt string) (AgentTurn, error)

// InMemoryAgentShell keeps a turn history with a built-in echo executor. Ports
// InMemoryAgentShell.
type InMemoryAgentShell struct {
	executor AgentExecutor
	mu       sync.Mutex
	history  []AgentTurn
	seq      int64
}

// NewInMemoryAgentShell constructs the shell; a nil executor uses the built-in
// deterministic responder. Ports the ctor.
func NewInMemoryAgentShell(executor AgentExecutor) *InMemoryAgentShell {
	if executor == nil {
		executor = builtInAgentExecutor
	}
	return &InMemoryAgentShell{executor: executor}
}

// BackendID returns "in-memory".
func (s *InMemoryAgentShell) BackendID() string { return "in-memory" }

// RunTurn executes the prompt, stamps a turn id if absent, and records it. Ports
// RunTurnAsync.
func (s *InMemoryAgentShell) RunTurn(ctx context.Context, userPrompt string) (AgentTurn, error) {
	t, err := s.executor(ctx, userPrompt)
	if err != nil {
		return AgentTurn{}, err
	}
	if t.TurnID == "" {
		t.TurnID = "turn-" + strconv.FormatInt(atomic.AddInt64(&s.seq, 1), 10)
	}
	s.mu.Lock()
	s.history = append(s.history, t)
	s.mu.Unlock()
	return t, nil
}

// History returns the most recent limit turns in chronological order. Ports
// HistoryAsync.
func (s *InMemoryAgentShell) History(ctx context.Context, limit int) ([]AgentTurn, error) {
	if limit <= 0 {
		return nil, errors.New("limit out of range")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	n := len(s.history)
	start := n - limit
	if start < 0 {
		start = 0
	}
	return append([]AgentTurn(nil), s.history[start:]...), nil
}

func builtInAgentExecutor(_ context.Context, prompt string) (AgentTurn, error) {
	trimmed := strings.TrimSpace(prompt)
	var response string
	switch {
	case len(trimmed) >= 5 && strings.EqualFold(trimmed[:5], "read "):
		response = "Reading " + trimmed[5:] + " ..."
	case len(trimmed) >= 6 && strings.EqualFold(trimmed[:6], "write "):
		response = "Writing " + trimmed[6:] + " ..."
	case strings.Contains(trimmed, "?"):
		response = "Acknowledged the question; need more context to give a useful answer."
	default:
		response = "Acknowledged: " + trimmed + "."
	}
	return AgentTurn{TurnID: "", UserPrompt: prompt, Response: response, Edits: []FileEdit{}}, nil
}

var _ IAgentShell = (*InMemoryAgentShell)(nil)

// ---------------------------------------------------------------------------
// PatternMatchPatchPlanner
// ---------------------------------------------------------------------------

var (
	patchRenameRx = regexp.MustCompile(`(?i)^rename\s+(\S+)\s+to\s+(\S+)(?:\s+in\s+(.+))?$`)
	patchRemoveRx = regexp.MustCompile(`(?i)^remove\s+line\s+(\d+)\s+from\s+(.+)$`)
	patchAppendRx = regexp.MustCompile(`(?i)^append\s+(.+?)\s+to\s+(.+)$`)
)

// PatternMatchPatchPlanner parses a goal and emits real FileEdits. Ports
// PatternMatchPatchPlanner.
type PatternMatchPatchPlanner struct {
	editor ICodeEditor
}

// NewPatternMatchPatchPlanner constructs the planner over an editor. Panics if
// editor is nil.
func NewPatternMatchPatchPlanner(editor ICodeEditor) *PatternMatchPatchPlanner {
	if editor == nil {
		panic("editor must not be nil")
	}
	return &PatternMatchPatchPlanner{editor: editor}
}

// BackendID returns "pattern-match".
func (p *PatternMatchPatchPlanner) BackendID() string { return "pattern-match" }

// Plan parses goal into a patch plan. Ports PlanAsync.
func (p *PatternMatchPatchPlanner) Plan(ctx context.Context, goal string) (PatchPlan, error) {
	if strings.TrimSpace(goal) == "" {
		return PatchPlan{}, errors.New("goal required")
	}
	if m := patchRenameRx.FindStringSubmatch(goal); m != nil {
		oldName, newName := m[1], m[2]
		scope := m[3]
		if scope == "" {
			cwd, _ := os.Getwd()
			scope = cwd
		}
		edits, err := computeRenameEdits(scope, oldName, newName)
		if err != nil {
			return PatchPlan{}, err
		}
		return PatchPlan{Goal: goal, Steps: []string{"Rename '" + oldName + "' -> '" + newName + "' across " + strconv.Itoa(len(edits)) + " location(s)"}, ProposedEdits: edits}, nil
	}
	if m := patchRemoveRx.FindStringSubmatch(goal); m != nil {
		lineNo, _ := strconv.Atoi(m[1])
		path := strings.TrimSpace(m[2])
		edits, err := computeRemoveLineEdits(path, lineNo)
		if err != nil {
			return PatchPlan{}, err
		}
		return PatchPlan{Goal: goal, Steps: []string{"Remove line " + strconv.Itoa(lineNo) + " from " + path}, ProposedEdits: edits}, nil
	}
	if m := patchAppendRx.FindStringSubmatch(goal); m != nil {
		text := strings.Trim(strings.TrimSpace(m[1]), "\"")
		path := strings.TrimSpace(m[2])
		length := 0
		if data, err := os.ReadFile(path); err == nil {
			length = len(data)
		}
		edits := []FileEdit{{Path: path, RangeStart: length, RangeEnd: length, Replacement: text}}
		return PatchPlan{Goal: goal, Steps: []string{"Append to " + path}, ProposedEdits: edits}, nil
	}
	return PatchPlan{Goal: goal, Steps: []string{"no recognised intent"}, ProposedEdits: []FileEdit{}}, nil
}

// Apply applies the plan's edits via the editor. Ports ApplyAsync.
func (p *PatternMatchPatchPlanner) Apply(ctx context.Context, plan PatchPlan) error {
	return p.editor.Apply(ctx, plan.ProposedEdits)
}

func computeRenameEdits(scope, oldName, newName string) ([]FileEdit, error) {
	fi, statErr := os.Stat(scope)
	if statErr != nil {
		return nil, errors.New("directory not found: " + scope)
	}
	var files []string
	sep := string(os.PathSeparator)
	if !fi.IsDir() {
		files = []string{scope}
	} else {
		_ = filepath.WalkDir(scope, func(path string, d os.DirEntry, err error) error {
			if err != nil || d.IsDir() {
				return nil
			}
			if strings.ToLower(filepath.Ext(path)) != ".cs" {
				return nil
			}
			if strings.Contains(path, sep+"obj"+sep) || strings.Contains(path, sep+"bin"+sep) {
				return nil
			}
			files = append(files, path)
			return nil
		})
	}
	edits := make([]FileEdit, 0)
	rx := regexp.MustCompile(`\b` + regexp.QuoteMeta(oldName) + `\b`)
	for _, f := range files {
		data, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		for _, loc := range rx.FindAllStringIndex(string(data), -1) {
			edits = append(edits, FileEdit{Path: f, RangeStart: loc[0], RangeEnd: loc[1], Replacement: newName})
		}
	}
	return edits, nil
}

func computeRemoveLineEdits(path string, lineNo int) ([]FileEdit, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	text := string(data)
	current := 1
	for i := 0; i < len(text); i++ {
		if current == lineNo {
			end := strings.IndexByte(text[i:], '\n')
			rangeEnd := len(text)
			if end >= 0 {
				rangeEnd = i + end + 1
			}
			return []FileEdit{{Path: path, RangeStart: i, RangeEnd: rangeEnd, Replacement: ""}}, nil
		}
		if text[i] == '\n' {
			current++
		}
	}
	return []FileEdit{}, nil
}

var _ IPatchPlanner = (*PatternMatchPatchPlanner)(nil)

// ---------------------------------------------------------------------------
// RegexRefactorTool
// ---------------------------------------------------------------------------

var (
	refactorRenameRx  = regexp.MustCompile(`(?i)^rename\s+(\S+)\s+to\s+(\S+)`)
	refactorExtractRx = regexp.MustCompile(`(?i)^extract\s+constant\s+from\s+"([^"]+)"\s+as\s+(\S+)`)
)

// RegexRefactorTool implements Rename + ExtractConstant refactors. Ports
// RegexRefactorTool.
type RegexRefactorTool struct{}

// BackendID returns "regex".
func (RegexRefactorTool) BackendID() string { return "regex" }

// Propose parses the request description and computes edits. Ports ProposeAsync.
func (RegexRefactorTool) Propose(ctx context.Context, request RefactorRequest) ([]FileEdit, error) {
	if request.TargetPaths == nil {
		return nil, errors.New("targetPaths required")
	}
	description := strings.TrimSpace(request.Description)
	if strings.HasPrefix(strings.ToLower(description), "rename ") {
		m := refactorRenameRx.FindStringSubmatch(description)
		if m == nil {
			return []FileEdit{}, nil
		}
		return renameInFiles(request.TargetPaths, m[1], m[2]), nil
	}
	if strings.HasPrefix(strings.ToLower(description), "extract ") {
		m := refactorExtractRx.FindStringSubmatch(description)
		if m == nil {
			return []FileEdit{}, nil
		}
		return extractConstant(request.TargetPaths, m[1], m[2]), nil
	}
	return []FileEdit{}, nil
}

func renameInFiles(paths []string, oldName, newName string) []FileEdit {
	edits := make([]FileEdit, 0)
	rx := regexp.MustCompile(`\b` + regexp.QuoteMeta(oldName) + `\b`)
	for _, p := range paths {
		data, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		for _, loc := range rx.FindAllStringIndex(string(data), -1) {
			edits = append(edits, FileEdit{Path: p, RangeStart: loc[0], RangeEnd: loc[1], Replacement: newName})
		}
	}
	return edits
}

func extractConstant(paths []string, literal, constantName string) []FileEdit {
	edits := make([]FileEdit, 0)
	quoted := "\"" + literal + "\""
	for _, p := range paths {
		data, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		text := string(data)
		first := strings.Index(text, quoted)
		if first < 0 {
			continue
		}
		classIdx := strings.Index(text, "class ")
		if classIdx < 0 {
			continue
		}
		brace := strings.IndexByte(text[classIdx:], '{')
		if brace < 0 {
			continue
		}
		brace += classIdx
		insertion := "\n    private const string " + constantName + " = " + quoted + ";\n"
		edits = append(edits, FileEdit{Path: p, RangeStart: brace + 1, RangeEnd: brace + 1, Replacement: insertion})
		for idx := first; idx >= 0; {
			edits = append(edits, FileEdit{Path: p, RangeStart: idx, RangeEnd: idx + len(quoted), Replacement: constantName})
			next := strings.Index(text[idx+1:], quoted)
			if next < 0 {
				break
			}
			idx = idx + 1 + next
		}
	}
	return edits
}

var _ IRefactorTool = RegexRefactorTool{}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullCodeEditor is a fail-closed editor. Ports NullCodeEditor.
type NullCodeEditor struct{}

// NullCodeEditorInstance is the shared singleton.
var NullCodeEditorInstance = NullCodeEditor{}

func (NullCodeEditor) BackendID() string                            { return "null" }
func (NullCodeEditor) Read(context.Context, string) (string, error) { return "", nil }
func (NullCodeEditor) Apply(context.Context, []FileEdit) error      { return nil }
func (NullCodeEditor) Save(context.Context, string) error           { return nil }

// NullInlineSuggester is a fail-closed suggester. Ports NullInlineSuggester.
type NullInlineSuggester struct{}

// NullInlineSuggesterInstance is the shared singleton.
var NullInlineSuggesterInstance = NullInlineSuggester{}

func (NullInlineSuggester) BackendID() string { return "null" }
func (NullInlineSuggester) Suggest(context.Context, string, int, int, string) (InlineSuggestion, bool, error) {
	return InlineSuggestion{}, false, nil
}

// NullAgentShell is a fail-closed shell. Ports NullAgentShell.
type NullAgentShell struct{}

// NullAgentShellInstance is the shared singleton.
var NullAgentShellInstance = NullAgentShell{}

func (NullAgentShell) BackendID() string { return "null" }
func (NullAgentShell) RunTurn(_ context.Context, prompt string) (AgentTurn, error) {
	return AgentTurn{TurnID: uuid.Nil.String(), UserPrompt: prompt, Response: "", Edits: []FileEdit{}}, nil
}
func (NullAgentShell) History(context.Context, int) ([]AgentTurn, error) {
	return []AgentTurn{}, nil
}

// NullPatchPlanner is a fail-closed planner. Ports NullPatchPlanner.
type NullPatchPlanner struct{}

// NullPatchPlannerInstance is the shared singleton.
var NullPatchPlannerInstance = NullPatchPlanner{}

func (NullPatchPlanner) BackendID() string { return "null" }
func (NullPatchPlanner) Plan(_ context.Context, goal string) (PatchPlan, error) {
	return PatchPlan{Goal: goal, Steps: []string{}, ProposedEdits: []FileEdit{}}, nil
}
func (NullPatchPlanner) Apply(context.Context, PatchPlan) error { return nil }

// NullRefactorTool is a fail-closed refactor tool. Ports NullRefactorTool.
type NullRefactorTool struct{}

// NullRefactorToolInstance is the shared singleton.
var NullRefactorToolInstance = NullRefactorTool{}

func (NullRefactorTool) BackendID() string { return "null" }
func (NullRefactorTool) Propose(context.Context, RefactorRequest) ([]FileEdit, error) {
	return []FileEdit{}, nil
}

var (
	_ ICodeEditor      = NullCodeEditor{}
	_ IInlineSuggester = NullInlineSuggester{}
	_ IAgentShell      = NullAgentShell{}
	_ IPatchPlanner    = NullPatchPlanner{}
	_ IRefactorTool    = NullRefactorTool{}
)
