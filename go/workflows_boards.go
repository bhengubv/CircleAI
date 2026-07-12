// workflows_boards.go
//
// Ports CircleAI.Workflows/PacaBoards.cs — the Sprintboard surface: status
// columns with position-ordered workflow, drag-and-drop status transitions,
// sprints with a Planning→Active→Completed lifecycle, per-task board metadata,
// per-view persistent configs, and lazy-load column pagination.
//
//	SprintState (enum)   -> int consts (Planning=0, Active=1, Completed=2)
//	StatusColumn / PacaSprint / TaskBoardMetadata / BoardView (records) -> structs
//	PacaBoard            -> PacaBoard over an *InMemoryPacaStore
//
// The C# (ProjectId, Number) tuple key is a "projectId|number" string here.
// GetOrCreateMetadata's defaults (Importance 3, StoryPoints 0, PositionInColumn
// 0) are preserved.

package circleai

import (
	"errors"
	"sort"
	"strconv"
	"sync"
	"time"
)

// SprintState is a sprint's lifecycle state. Ports SprintState
// (declaration-order ordinals: Planning=0, Active=1, Completed=2).
type SprintState int

const (
	// SprintStatePlanning — created, not started.
	SprintStatePlanning SprintState = 0
	// SprintStateActive — running.
	SprintStateActive SprintState = 1
	// SprintStateCompleted — closed.
	SprintStateCompleted SprintState = 2
)

// StatusColumn is a status column in the workflow. Ports the StatusColumn
// record. Category is "open"/"in-flight"/"review"/"closed"/"cancelled"/"blocked".
type StatusColumn struct {
	Name      string
	Category  string
	Position  int
	Collapsed bool
}

// PacaSprint is a sprint. Ports the PacaSprint record.
type PacaSprint struct {
	ID        string
	ProjectID string
	Name      string
	Goal      string
	StartDate time.Time
	EndDate   time.Time
	State     SprintState
}

// TaskBoardMetadata is extra board-only metadata on top of a PacaTask. Ports
// the TaskBoardMetadata record. AssigneeMemberID / ReporterMemberID / SprintID
// are empty and ParentTaskNumber is nil when unset (C# nullable fields).
type TaskBoardMetadata struct {
	ProjectID        string
	Number           int
	StoryPoints      int
	Importance       int // 0..5
	AssigneeMemberID string
	ReporterMemberID string
	ParentTaskNumber *int
	SprintID         string
	Tags             []string
	CustomFields     map[string]string
	PositionInColumn int
}

// BoardView is a per-user / per-board named view. Ports the BoardView record.
// SortBy is "importance"/"story_points"/"newest".
type BoardView struct {
	Name           string
	FilterTagsCSV  string
	FilterAssignee string
	SortBy         string
	SortDescending bool
	VisibleColumns []string
	VisibleFields  []string
}

// PacaBoard is a board service over a project: sprints + columns + per-task
// metadata + views. Ports PacaBoard. Construct with NewPacaBoard.
type PacaBoard struct {
	tasks    *InMemoryPacaStore
	mu       sync.Mutex
	columns  map[string]StatusColumn
	sprints  map[string]PacaSprint
	metadata map[string]TaskBoardMetadata // key = projectID + "|" + number
	views    map[string]BoardView
	clock    func() time.Time
}

// NewPacaBoard constructs a board over tasks, seeding the six default columns.
// clock may be nil (defaults to UTC now). Panics if tasks is nil.
func NewPacaBoard(tasks *InMemoryPacaStore, clock func() time.Time) *PacaBoard {
	if tasks == nil {
		panic("tasks must not be nil")
	}
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	b := &PacaBoard{
		tasks:    tasks,
		columns:  make(map[string]StatusColumn),
		sprints:  make(map[string]PacaSprint),
		metadata: make(map[string]TaskBoardMetadata),
		views:    make(map[string]BoardView),
		clock:    clock,
	}
	b.addDefaultColumns()
	return b
}

func (b *PacaBoard) addDefaultColumns() {
	b.columns["todo"] = StatusColumn{"todo", "open", 0, false}
	b.columns["in_progress"] = StatusColumn{"in_progress", "in-flight", 1, false}
	b.columns["in_review"] = StatusColumn{"in_review", "review", 2, false}
	b.columns["done"] = StatusColumn{"done", "closed", 3, false}
	b.columns["cancelled"] = StatusColumn{"cancelled", "cancelled", 4, false}
	b.columns["blocked"] = StatusColumn{"blocked", "blocked", 5, true}
}

func metaKey(projectID string, number int) string {
	return projectID + "|" + strconv.Itoa(number)
}

// Columns returns the columns ordered by position. Ports the Columns property.
func (b *PacaBoard) Columns() []StatusColumn {
	b.mu.Lock()
	out := make([]StatusColumn, 0, len(b.columns))
	for _, c := range b.columns {
		out = append(out, c)
	}
	b.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Position < out[j].Position })
	return out
}

// AddColumn adds (or replaces by Name) a column. Ports AddColumn.
func (b *PacaBoard) AddColumn(col StatusColumn) {
	b.mu.Lock()
	b.columns[col.Name] = col
	b.mu.Unlock()
}

// CollapseColumn sets a column's collapsed flag. Ports CollapseColumn. No-op if
// the column is unknown.
func (b *PacaBoard) CollapseColumn(name string, collapsed bool) {
	b.mu.Lock()
	if col, ok := b.columns[name]; ok {
		col.Collapsed = collapsed
		b.columns[name] = col
	}
	b.mu.Unlock()
}

// MoveTask moves a task between status columns, updating its in-column position.
// Ports MoveTask. Returns an error if the task is not found or the status is
// unknown.
func (b *PacaBoard) MoveTask(projectID string, number int, newStatus string, newPosition int) error {
	task, ok := b.tasks.GetTaskByReference(projectID, projectID+"-"+strconv.Itoa(number))
	if !ok {
		for _, t := range b.tasks.ListTasks(projectID) {
			if t.Number == number {
				task, ok = t, true
				break
			}
		}
	}
	if !ok {
		return errors.New("Task not found.")
	}
	b.mu.Lock()
	_, known := b.columns[newStatus]
	b.mu.Unlock()
	if !known {
		return errors.New("Unknown status '" + newStatus + "'.")
	}
	task.Status = newStatus
	b.tasks.UpdateTask(task)

	b.mu.Lock()
	meta := b.getOrCreateMetadataLocked(projectID, number)
	meta.PositionInColumn = newPosition
	b.metadata[metaKey(projectID, number)] = meta
	b.mu.Unlock()
	return nil
}

// SetTaskMetadata attaches board metadata to an existing task. Ports
// SetTaskMetadata.
func (b *PacaBoard) SetTaskMetadata(metadata TaskBoardMetadata) {
	b.mu.Lock()
	b.metadata[metaKey(metadata.ProjectID, metadata.Number)] = metadata
	b.mu.Unlock()
}

// GetTaskMetadata returns a task's board metadata and true, or (zero, false).
// Ports GetTaskMetadata.
func (b *PacaBoard) GetTaskMetadata(projectID string, number int) (TaskBoardMetadata, bool) {
	b.mu.Lock()
	m, ok := b.metadata[metaKey(projectID, number)]
	b.mu.Unlock()
	return m, ok
}

// TasksInColumn is a paginated column read for lazy loading. Ports
// TasksInColumn. take<=0 disables the limit.
func (b *PacaBoard) TasksInColumn(projectID, status string, skip, take int) []PacaTask {
	live := b.tasks.ListTasks(projectID)
	filtered := make([]PacaTask, 0, len(live))
	for _, t := range live {
		if t.Status == status {
			filtered = append(filtered, t)
		}
	}
	b.mu.Lock()
	sort.SliceStable(filtered, func(i, j int) bool {
		return b.getOrCreateMetadataLocked(filtered[i].ProjectID, filtered[i].Number).PositionInColumn <
			b.getOrCreateMetadataLocked(filtered[j].ProjectID, filtered[j].Number).PositionInColumn
	})
	b.mu.Unlock()
	if skip < 0 {
		skip = 0
	}
	if skip >= len(filtered) {
		return []PacaTask{}
	}
	filtered = filtered[skip:]
	if take > 0 && take < len(filtered) {
		filtered = filtered[:take]
	}
	return filtered
}

// TasksInSprint returns the tasks bucketed into a sprint. Ports TasksInSprint.
func (b *PacaBoard) TasksInSprint(sprintID string) []PacaTask {
	b.mu.Lock()
	type ref struct {
		projectID string
		number    int
	}
	refs := make([]ref, 0)
	for _, m := range b.metadata {
		if m.SprintID == sprintID {
			refs = append(refs, ref{m.ProjectID, m.Number})
		}
	}
	b.mu.Unlock()

	out := make([]PacaTask, 0, len(refs))
	for _, r := range refs {
		for _, t := range b.tasks.ListTasks(r.projectID) {
			if t.Number == r.number {
				out = append(out, t)
				break
			}
		}
	}
	return out
}

// CreateSprint creates a sprint in Planning. Ports CreateSprint.
func (b *PacaBoard) CreateSprint(id, projectID, name, goal string, start, end time.Time) PacaSprint {
	s := PacaSprint{ID: id, ProjectID: projectID, Name: name, Goal: goal, StartDate: start, EndDate: end, State: SprintStatePlanning}
	b.mu.Lock()
	b.sprints[id] = s
	b.mu.Unlock()
	return s
}

// GetSprint returns a sprint and true, or (zero, false). Ports GetSprint.
func (b *PacaBoard) GetSprint(id string) (PacaSprint, bool) {
	b.mu.Lock()
	s, ok := b.sprints[id]
	b.mu.Unlock()
	return s, ok
}

// StartSprint transitions a sprint to Active. Ports StartSprint.
func (b *PacaBoard) StartSprint(id string) (PacaSprint, error) {
	return b.transition(id, SprintStateActive)
}

// CompleteSprint transitions a sprint to Completed. Ports CompleteSprint.
func (b *PacaBoard) CompleteSprint(id string) (PacaSprint, error) {
	return b.transition(id, SprintStateCompleted)
}

func (b *PacaBoard) transition(id string, to SprintState) (PacaSprint, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	sprint, ok := b.sprints[id]
	if !ok {
		return PacaSprint{}, errors.New("Sprint '" + id + "' not found.")
	}
	sprint.State = to
	b.sprints[id] = sprint
	return sprint, nil
}

// SaveView saves a named view. Ports SaveView.
func (b *PacaBoard) SaveView(view BoardView) {
	b.mu.Lock()
	b.views[view.Name] = view
	b.mu.Unlock()
}

// GetView returns a named view and true, or (zero, false). Ports GetView.
func (b *PacaBoard) GetView(name string) (BoardView, bool) {
	b.mu.Lock()
	v, ok := b.views[name]
	b.mu.Unlock()
	return v, ok
}

// ListViews returns the views ordered by name. Ports ListViews.
func (b *PacaBoard) ListViews() []BoardView {
	b.mu.Lock()
	out := make([]BoardView, 0, len(b.views))
	for _, v := range b.views {
		out = append(out, v)
	}
	b.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// getOrCreateMetadataLocked returns the metadata for (projectID, number),
// creating a default entry (Importance 3) if absent. Caller holds b.mu.
func (b *PacaBoard) getOrCreateMetadataLocked(projectID string, number int) TaskBoardMetadata {
	key := metaKey(projectID, number)
	if m, ok := b.metadata[key]; ok {
		return m
	}
	m := TaskBoardMetadata{
		ProjectID:    projectID,
		Number:       number,
		StoryPoints:  0,
		Importance:   3,
		Tags:         []string{},
		CustomFields: map[string]string{},
	}
	b.metadata[key] = m
	return m
}
