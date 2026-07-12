// workflows_docs.go
//
// Ports CircleAI.Workflows/PacaDocs.cs — project-level living documents with
// folders, immutable version snapshots, an activity feed, task/epic linkage,
// and @mentions of humans + agents.
//
//	DocNode / DocVersion / DocActivity / DocLink (records) -> structs
//	PacaDocService -> PacaDocService
//
// Edit snapshots the PRE-edit content as a version (matching C#: the version
// records node.ContentJson before the update is applied), then appends an
// "edited"/"ai-edited" activity, and returns the deduped mention handles found
// in the NEW content. Mentions match @([a-zA-Z0-9_-]+), case-insensitively
// deduplicated.

package circleai

import (
	"errors"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// DocNode is a doc-tree node (folder OR document). Ports the DocNode record.
// ParentID is empty for a root node; DeletedAtUTC is nil for a live node.
type DocNode struct {
	ID           string
	ProjectID    string
	ParentID     string
	IsFolder     bool
	Title        string
	ContentJSON  string
	CreatedAtUTC time.Time
	DeletedAtUTC *time.Time
}

// DocVersion is one immutable snapshot of a doc. Ports the DocVersion record.
type DocVersion struct {
	VersionID      string
	DocID          string
	ContentJSON    string
	SavedAtUTC     time.Time
	AuthorMemberID string
}

// DocActivity is one document-activity event. Ports the DocActivity record.
// Action is "created"/"edited"/"ai-edited"/"linked"/"commented". Detail is
// empty when unset.
type DocActivity struct {
	ActivityID     string
	DocID          string
	AuthorMemberID string
	Action         string
	Detail         string
	At             time.Time
}

// DocLink is a link between a doc section and a task/epic. Ports the DocLink
// record.
type DocLink struct {
	LinkID        string
	DocID         string
	SectionAnchor string
	ProjectID     string
	TaskNumber    int
}

var docMentionPattern = regexp.MustCompile(`@([a-zA-Z0-9_\-]+)`)

// PacaDocService is an in-memory doc service. Ports PacaDocService. Construct
// with NewPacaDocService.
type PacaDocService struct {
	mu       sync.Mutex
	nodes    map[string]DocNode
	versions map[string][]DocVersion
	activity map[string][]DocActivity
	links    map[string][]DocLink
	clock    func() time.Time
}

// NewPacaDocService constructs an empty doc service. clock may be nil.
func NewPacaDocService(clock func() time.Time) *PacaDocService {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &PacaDocService{
		nodes:    make(map[string]DocNode),
		versions: make(map[string][]DocVersion),
		activity: make(map[string][]DocActivity),
		links:    make(map[string][]DocLink),
		clock:    clock,
	}
}

// CreateFolder creates a folder node. Ports CreateFolder.
func (s *PacaDocService) CreateFolder(id, projectID, parentID, title string) (DocNode, error) {
	return s.create(id, projectID, parentID, true, title, "{}", "system")
}

// CreateDocument creates a document node, seeding its version list + a "created"
// activity. Ports CreateDocument.
func (s *PacaDocService) CreateDocument(id, projectID, parentID, title, contentJSON, authorMemberID string) (DocNode, error) {
	return s.create(id, projectID, parentID, false, title, contentJSON, authorMemberID)
}

func (s *PacaDocService) create(id, projectID, parentID string, isFolder bool, title, contentJSON, authorMemberID string) (DocNode, error) {
	if strings.TrimSpace(id) == "" {
		return DocNode{}, errors.New("id required")
	}
	if strings.TrimSpace(projectID) == "" {
		return DocNode{}, errors.New("projectId required")
	}
	if contentJSON == "" {
		contentJSON = "{}"
	}
	node := DocNode{
		ID:           id,
		ProjectID:    projectID,
		ParentID:     parentID,
		IsFolder:     isFolder,
		Title:        title,
		ContentJSON:  contentJSON,
		CreatedAtUTC: s.clock(),
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.nodes[id]; exists {
		return DocNode{}, errors.New("Doc '" + id + "' already exists.")
	}
	s.nodes[id] = node
	if !isFolder {
		s.versions[id] = make([]DocVersion, 0)
		s.activity[id] = []DocActivity{{
			ActivityID:     newHexGUID(),
			DocID:          id,
			AuthorMemberID: authorMemberID,
			Action:         "created",
			At:             s.clock(),
		}}
	}
	return node, nil
}

// Get returns a live node and true, or (zero, false). Ports Get.
func (s *PacaDocService) Get(id string) (DocNode, bool) {
	s.mu.Lock()
	n, ok := s.nodes[id]
	s.mu.Unlock()
	if !ok || n.DeletedAtUTC != nil {
		return DocNode{}, false
	}
	return n, true
}

// ListChildren lists live children under parentID (empty parentID = roots),
// ordered by title. Ports ListChildren.
func (s *PacaDocService) ListChildren(projectID, parentID string) []DocNode {
	s.mu.Lock()
	out := make([]DocNode, 0)
	for _, n := range s.nodes {
		if n.ProjectID == projectID && n.ParentID == parentID && n.DeletedAtUTC == nil {
			out = append(out, n)
		}
	}
	s.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Title < out[j].Title })
	return out
}

// Edit edits a document: writes a version of the pre-edit content, appends an
// activity entry, and returns the deduped mention handles in the new content.
// Ports Edit. Returns an error if the doc is not an editable live document.
func (s *PacaDocService) Edit(id, newContentJSON, authorMemberID string, isAIEdit bool) ([]string, error) {
	if newContentJSON == "" {
		newContentJSON = "{}"
	}
	s.mu.Lock()
	node, ok := s.nodes[id]
	if !ok || node.IsFolder || node.DeletedAtUTC != nil {
		s.mu.Unlock()
		return nil, errors.New("Doc '" + id + "' is not editable.")
	}
	priorContent := node.ContentJSON
	node.ContentJSON = newContentJSON
	s.nodes[id] = node

	s.versions[id] = append(s.versions[id], DocVersion{
		VersionID:      newHexGUID(),
		DocID:          id,
		ContentJSON:    priorContent,
		SavedAtUTC:     s.clock(),
		AuthorMemberID: authorMemberID,
	})
	action := "edited"
	if isAIEdit {
		action = "ai-edited"
	}
	s.activity[id] = append(s.activity[id], DocActivity{
		ActivityID:     newHexGUID(),
		DocID:          id,
		AuthorMemberID: authorMemberID,
		Action:         action,
		At:             s.clock(),
	})
	s.mu.Unlock()
	return extractMentions(newContentJSON), nil
}

// Versions returns a snapshot of a doc's versions. Ports Versions.
func (s *PacaDocService) Versions(docID string) []DocVersion {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := s.versions[docID]
	out := make([]DocVersion, len(list))
	copy(out, list)
	return out
}

// DiffLines is a cheap diff between two versions: returns added + removed text
// lines (set difference, unordered). Ports DiffLines.
func (s *PacaDocService) DiffLines(before, after string) (added, removed []string) {
	b := lineSet(before)
	a := lineSet(after)
	added = make([]string, 0)
	removed = make([]string, 0)
	for line := range a {
		if _, ok := b[line]; !ok {
			added = append(added, line)
		}
	}
	for line := range b {
		if _, ok := a[line]; !ok {
			removed = append(removed, line)
		}
	}
	return added, removed
}

// Activity returns a snapshot of a doc's activity feed. Ports Activity.
func (s *PacaDocService) Activity(docID string) []DocActivity {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := s.activity[docID]
	out := make([]DocActivity, len(list))
	copy(out, list)
	return out
}

// Link links a doc section to a task, appending a "linked" activity. Ports Link.
func (s *PacaDocService) Link(docID, sectionAnchor, projectID string, taskNumber int) DocLink {
	link := DocLink{
		LinkID:        newHexGUID(),
		DocID:         docID,
		SectionAnchor: sectionAnchor,
		ProjectID:     projectID,
		TaskNumber:    taskNumber,
	}
	s.mu.Lock()
	s.links[docID] = append(s.links[docID], link)
	s.activity[docID] = append(s.activity[docID], DocActivity{
		ActivityID:     newHexGUID(),
		DocID:          docID,
		AuthorMemberID: "system",
		Action:         "linked",
		Detail:         projectID + "-" + strconv.Itoa(taskNumber) + "@" + sectionAnchor,
		At:             s.clock(),
	})
	s.mu.Unlock()
	return link
}

// Links returns a snapshot of a doc's links. Ports Links.
func (s *PacaDocService) Links(docID string) []DocLink {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := s.links[docID]
	out := make([]DocLink, len(list))
	copy(out, list)
	return out
}

func lineSet(s string) map[string]struct{} {
	m := make(map[string]struct{})
	for _, line := range strings.Split(s, "\n") {
		m[line] = struct{}{}
	}
	return m
}

// extractMentions returns the deduped (case-insensitive) @handles in content,
// preserving first-seen order. Ports ExtractMentions.
func extractMentions(content string) []string {
	matches := docMentionPattern.FindAllStringSubmatch(content, -1)
	seen := make(map[string]struct{})
	out := make([]string, 0)
	for _, m := range matches {
		handle := m[1]
		key := strings.ToLower(handle)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, handle)
	}
	return out
}
