// workflows_projects.go
//
// Ports CircleAI.Workflows/PacaProjects.cs — project + task primitives from
// paca. Auto-generates task ids as <PREFIX>-N, soft-deletes via a nil
// DeletedAtUTC, and scopes every query by projectID.
//
//	PacaProject / PacaTask (records)  -> structs (DeletedAtUTC as *time.Time)
//	InMemoryPacaStore                  -> InMemoryPacaStore (real, mutex-guarded)
//
// C# nullable DateTimeOffset? maps to *time.Time (nil = live). The C#
// ConcurrentDictionary + per-list lock is a single sync.Mutex here — the store
// is small and every op is O(n) over a project's task list anyway.

package circleai

import (
	"errors"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// PacaProject is a workspace that contains tasks. Ports the PacaProject record.
// DeletedAtUTC is nil for a live project (C# nullable DateTimeOffset?).
type PacaProject struct {
	ID           string
	Name         string
	Prefix       string
	SettingsJSON string
	CreatedAtUTC time.Time
	DeletedAtUTC *time.Time
}

// PacaTask is a unit of work inside a project. Ports the PacaTask record.
// Number is the sequential per-project id (PACA-1, PACA-2, …).
type PacaTask struct {
	ProjectID       string
	Number          int
	Title           string
	DescriptionJSON string
	Status          string
	CreatedAtUTC    time.Time
	DeletedAtUTC    *time.Time
}

// Reference renders the task's human reference like "PACA-3". Ports Reference.
func (t PacaTask) Reference(prefix string) string {
	return prefix + "-" + strconv.Itoa(t.Number)
}

// InMemoryPacaStore is an in-memory project + task store. Ports
// InMemoryPacaStore. Construct with NewInMemoryPacaStore.
type InMemoryPacaStore struct {
	mu             sync.Mutex
	projects       map[string]PacaProject
	tasksByProject map[string][]PacaTask
	nextNumber     map[string]int
	clock          func() time.Time
}

// NewInMemoryPacaStore constructs an empty store. clock may be nil (defaults to
// UTC now).
func NewInMemoryPacaStore(clock func() time.Time) *InMemoryPacaStore {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &InMemoryPacaStore{
		projects:       make(map[string]PacaProject),
		tasksByProject: make(map[string][]PacaTask),
		nextNumber:     make(map[string]int),
		clock:          clock,
	}
}

// CreateProject creates a new project. Ports CreateProject. Returns an error if
// a required field is blank or the id already exists.
func (s *InMemoryPacaStore) CreateProject(id, name, prefix, settingsJSON string) (PacaProject, error) {
	if strings.TrimSpace(id) == "" {
		return PacaProject{}, errors.New("id required")
	}
	if strings.TrimSpace(name) == "" {
		return PacaProject{}, errors.New("name required")
	}
	if strings.TrimSpace(prefix) == "" {
		return PacaProject{}, errors.New("prefix required")
	}
	if settingsJSON == "" {
		settingsJSON = "{}"
	}
	project := PacaProject{
		ID:           id,
		Name:         name,
		Prefix:       prefix,
		SettingsJSON: settingsJSON,
		CreatedAtUTC: s.clock(),
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.projects[id]; exists {
		return PacaProject{}, errors.New("Project '" + id + "' already exists.")
	}
	s.projects[id] = project
	s.tasksByProject[id] = make([]PacaTask, 0)
	s.nextNumber[id] = 1
	return project, nil
}

// GetProject returns a live project by id and true (excludes soft-deleted).
// Ports GetProject.
func (s *InMemoryPacaStore) GetProject(id string) (PacaProject, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.getProjectLocked(id)
}

func (s *InMemoryPacaStore) getProjectLocked(id string) (PacaProject, bool) {
	p, ok := s.projects[id]
	if !ok || p.DeletedAtUTC != nil {
		return PacaProject{}, false
	}
	return p, true
}

// DeleteProject soft-deletes a project. Idempotent. Ports DeleteProject.
func (s *InMemoryPacaStore) DeleteProject(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	existing, ok := s.projects[id]
	if !ok || existing.DeletedAtUTC != nil {
		return
	}
	now := s.clock()
	existing.DeletedAtUTC = &now
	s.projects[id] = existing
}

// UpdateProjectSettings replaces the JSON settings bag. Ports
// UpdateProjectSettings. Returns an error if the project is not found.
func (s *InMemoryPacaStore) UpdateProjectSettings(projectID, newSettingsJSON string) (PacaProject, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	existing, ok := s.getProjectLocked(projectID)
	if !ok {
		return PacaProject{}, errors.New("Project '" + projectID + "' not found.")
	}
	if newSettingsJSON == "" {
		newSettingsJSON = "{}"
	}
	existing.SettingsJSON = newSettingsJSON
	s.projects[projectID] = existing
	return existing, nil
}

// AddTask adds an auto-numbered task to a project. Ports AddTask. Returns an
// error if the project is not found. Empty status defaults to "todo".
func (s *InMemoryPacaStore) AddTask(projectID, title, descriptionJSON, status string) (PacaTask, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.getProjectLocked(projectID); !ok {
		return PacaTask{}, errors.New("Project '" + projectID + "' not found.")
	}
	number := s.nextNumber[projectID]
	s.nextNumber[projectID] = number + 1
	if descriptionJSON == "" {
		descriptionJSON = "{}"
	}
	if status == "" {
		status = "todo"
	}
	task := PacaTask{
		ProjectID:       projectID,
		Number:          number,
		Title:           title,
		DescriptionJSON: descriptionJSON,
		Status:          status,
		CreatedAtUTC:    s.clock(),
	}
	s.tasksByProject[projectID] = append(s.tasksByProject[projectID], task)
	return task, nil
}

// ListTasks lists live tasks for a project, ordered by number ascending. Ports
// ListTasks.
func (s *InMemoryPacaStore) ListTasks(projectID string) []PacaTask {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.listTasksLocked(projectID)
}

func (s *InMemoryPacaStore) listTasksLocked(projectID string) []PacaTask {
	list, ok := s.tasksByProject[projectID]
	if !ok {
		return []PacaTask{}
	}
	out := make([]PacaTask, 0, len(list))
	for _, t := range list {
		if t.DeletedAtUTC == nil {
			out = append(out, t)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Number < out[j].Number })
	return out
}

// GetTaskByReference finds one live task by a reference like "PACA-3". Ports
// GetTaskByReference. Returns (zero, false) on any mismatch.
func (s *InMemoryPacaStore) GetTaskByReference(projectID, reference string) (PacaTask, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	project, ok := s.getProjectLocked(projectID)
	if !ok {
		return PacaTask{}, false
	}
	expectedPrefix := project.Prefix + "-"
	if !strings.HasPrefix(strings.ToLower(reference), strings.ToLower(expectedPrefix)) {
		return PacaTask{}, false
	}
	n, err := strconv.Atoi(reference[len(expectedPrefix):])
	if err != nil {
		return PacaTask{}, false
	}
	for _, t := range s.tasksByProject[projectID] {
		if t.Number == n && t.DeletedAtUTC == nil {
			return t, true
		}
	}
	return PacaTask{}, false
}

// UpdateTask updates a task in place (matched by ProjectID + Number). Ports
// UpdateTask. No-op if the project or task is unknown.
func (s *InMemoryPacaStore) UpdateTask(updated PacaTask) {
	s.mu.Lock()
	defer s.mu.Unlock()
	list, ok := s.tasksByProject[updated.ProjectID]
	if !ok {
		return
	}
	for i := range list {
		if list[i].Number == updated.Number {
			list[i] = updated
			return
		}
	}
}

// DeleteTask soft-deletes a task. Ports DeleteTask. No-op if unknown.
func (s *InMemoryPacaStore) DeleteTask(projectID string, number int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	list, ok := s.tasksByProject[projectID]
	if !ok {
		return
	}
	for i := range list {
		if list[i].Number == number {
			now := s.clock()
			list[i].DeletedAtUTC = &now
			return
		}
	}
}
