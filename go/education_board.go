// education_board.go
//
// Ports the CircleAI.Education primitive vertical (EducationPrimitives.cs):
//   Course / Lesson / StudentRecord (records) -> value structs
//   IEducationBoard        -> EducationBoard interface (I-prefix dropped)
//   InMemoryEducationBoard -> InMemoryEducationBoard
//
// The EducationDomainContext (static prompt strings) and
// EducationCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board and are not ported.
//
// DETERMINISM: lessons are ordered by OrderIndex ascending; StudentsFor keeps
// no defined C# order (ConcurrentDictionary values) so this port sorts by
// StudentId for stable output. Lesson OrderIndex ties break by LessonId.
// AvgProgressFor reproduces LINQ Average over ProgressPct (0.0 on no students).

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Course is a course. Ports the Course record.
type Course struct {
	CourseId  string
	Name      string
	Subject   string
	GradeBand string
}

// Lesson is a lesson within a course. Ports the Lesson record. Duration mirrors
// the C# TimeSpan.
type Lesson struct {
	LessonId   string
	CourseId   string
	Title      string
	Duration   time.Duration
	OrderIndex int
}

// StudentRecord is a student's enrolment + progress in a course. Ports the
// StudentRecord record. ProgressPct is a percentage (0..100).
type StudentRecord struct {
	StudentId   string
	Name        string
	CourseId    string
	ProgressPct float64
}

// EducationBoard is the courses/lessons/enrolments board. Ports IEducationBoard.
type EducationBoard interface {
	AddCourse(c Course)
	GetCourse(id string) (Course, bool)
	AddLesson(l Lesson)
	// LessonsFor lists a course's lessons ordered by OrderIndex ascending.
	LessonsFor(courseId string) []Lesson
	Enrol(r StudentRecord)
	// UpdateProgress sets a student's progress; errors if the id is unknown.
	UpdateProgress(studentId string, pct float64) error
	// StudentsFor lists students enrolled in a course.
	StudentsFor(courseId string) []StudentRecord
	// AvgProgressFor is the mean ProgressPct across a course's students (0 if none).
	AvgProgressFor(courseId string) float64
}

// InMemoryEducationBoard is a concurrency-safe in-memory EducationBoard. Ports
// InMemoryEducationBoard.
type InMemoryEducationBoard struct {
	mu       sync.RWMutex
	courses  map[string]Course
	lessons  map[string]Lesson
	students map[string]StudentRecord
}

// NewInMemoryEducationBoard constructs an empty board.
func NewInMemoryEducationBoard() *InMemoryEducationBoard {
	return &InMemoryEducationBoard{
		courses:  make(map[string]Course),
		lessons:  make(map[string]Lesson),
		students: make(map[string]StudentRecord),
	}
}

// AddCourse stores (or replaces by CourseId) a course. Ports AddCourse.
func (b *InMemoryEducationBoard) AddCourse(c Course) {
	b.mu.Lock()
	b.courses[c.CourseId] = c
	b.mu.Unlock()
}

// GetCourse returns the course for id and true, or (zero, false) if absent.
func (b *InMemoryEducationBoard) GetCourse(id string) (Course, bool) {
	b.mu.RLock()
	c, ok := b.courses[id]
	b.mu.RUnlock()
	return c, ok
}

// AddLesson stores (or replaces by LessonId) a lesson. Ports AddLesson.
func (b *InMemoryEducationBoard) AddLesson(l Lesson) {
	b.mu.Lock()
	b.lessons[l.LessonId] = l
	b.mu.Unlock()
}

// LessonsFor lists a course's lessons ordered by OrderIndex ascending. Ports
// LessonsFor.
func (b *InMemoryEducationBoard) LessonsFor(courseId string) []Lesson {
	b.mu.RLock()
	out := make([]Lesson, 0)
	for _, l := range b.lessons {
		if l.CourseId == courseId {
			out = append(out, l)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].OrderIndex != out[j].OrderIndex {
			return out[i].OrderIndex < out[j].OrderIndex
		}
		return out[i].LessonId < out[j].LessonId
	})
	return out
}

// Enrol stores (or replaces by StudentId) a student record. Ports Enrol.
func (b *InMemoryEducationBoard) Enrol(r StudentRecord) {
	b.mu.Lock()
	b.students[r.StudentId] = r
	b.mu.Unlock()
}

// UpdateProgress mutates a student's ProgressPct. Ports UpdateProgress (throws on
// unknown id -> error).
func (b *InMemoryEducationBoard) UpdateProgress(studentId string, pct float64) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	r, ok := b.students[studentId]
	if !ok {
		return errors.New("Unknown student " + studentId)
	}
	r.ProgressPct = pct
	b.students[studentId] = r
	return nil
}

// StudentsFor lists students in a course (sorted by StudentId for determinism).
// Ports StudentsFor.
func (b *InMemoryEducationBoard) StudentsFor(courseId string) []StudentRecord {
	b.mu.RLock()
	out := make([]StudentRecord, 0)
	for _, s := range b.students {
		if s.CourseId == courseId {
			out = append(out, s)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].StudentId < out[j].StudentId })
	return out
}

// AvgProgressFor returns the mean ProgressPct over a course's students, or 0 when
// there are none. Ports AvgProgressFor.
func (b *InMemoryEducationBoard) AvgProgressFor(courseId string) float64 {
	rows := b.StudentsFor(courseId)
	if len(rows) == 0 {
		return 0.0
	}
	var sum float64
	for _, r := range rows {
		sum += r.ProgressPct
	}
	return sum / float64(len(rows))
}

// Interface guard.
var _ EducationBoard = (*InMemoryEducationBoard)(nil)
