// education_board_test.go
//
// Verifies the CircleAI.Education port (education_board.go): course add/get,
// lesson ordering by OrderIndex, enrol + progress update, students-for filter,
// and average-progress (including the empty-course 0.0 case).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestEducation_CourseAndLessons(t *testing.T) {
	b := circleai.NewInMemoryEducationBoard()
	b.AddCourse(circleai.Course{CourseId: "c1", Name: "Algebra", Subject: "Math", GradeBand: "8-9"})
	if got, ok := b.GetCourse("c1"); !ok || got.Name != "Algebra" {
		t.Fatalf("get course = %+v ok=%v", got, ok)
	}
	if _, ok := b.GetCourse("none"); ok {
		t.Fatalf("missing course found")
	}
	b.AddLesson(circleai.Lesson{LessonId: "l3", CourseId: "c1", Title: "Third", Duration: 30 * time.Minute, OrderIndex: 3})
	b.AddLesson(circleai.Lesson{LessonId: "l1", CourseId: "c1", Title: "First", Duration: 20 * time.Minute, OrderIndex: 1})
	b.AddLesson(circleai.Lesson{LessonId: "l2", CourseId: "c1", Title: "Second", Duration: 25 * time.Minute, OrderIndex: 2})
	b.AddLesson(circleai.Lesson{LessonId: "x1", CourseId: "other", Title: "Other", OrderIndex: 1})

	lessons := b.LessonsFor("c1")
	if len(lessons) != 3 || lessons[0].LessonId != "l1" || lessons[1].LessonId != "l2" || lessons[2].LessonId != "l3" {
		t.Fatalf("lessons ordered by index failed: %+v", lessons)
	}
}

func TestEducation_EnrolProgressAndAverage(t *testing.T) {
	b := circleai.NewInMemoryEducationBoard()
	b.Enrol(circleai.StudentRecord{StudentId: "s1", Name: "A", CourseId: "c1", ProgressPct: 20})
	b.Enrol(circleai.StudentRecord{StudentId: "s2", Name: "B", CourseId: "c1", ProgressPct: 40})
	b.Enrol(circleai.StudentRecord{StudentId: "s3", Name: "C", CourseId: "c2", ProgressPct: 90})

	if err := b.UpdateProgress("s1", 60); err != nil {
		t.Fatalf("update progress: %v", err)
	}
	if err := b.UpdateProgress("ghost", 10); err == nil {
		t.Fatalf("unknown student update must error")
	}
	students := b.StudentsFor("c1")
	if len(students) != 2 || students[0].StudentId != "s1" || students[1].StudentId != "s2" {
		t.Fatalf("students-for failed: %+v", students)
	}
	// Avg of 60 and 40 = 50.
	if avg := b.AvgProgressFor("c1"); math.Abs(avg-50.0) > 1e-9 {
		t.Fatalf("avg = %v, want 50", avg)
	}
	// Empty course -> 0.0.
	if avg := b.AvgProgressFor("empty"); avg != 0.0 {
		t.Fatalf("empty avg = %v, want 0", avg)
	}
}
