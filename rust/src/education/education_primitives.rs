//! education_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the Education vertical — Rust
//! port of `src/CircleAI.Education/EducationPrimitives.cs`: courses, lessons,
//! student records, progress tracking.
//!
//! `TimeSpan Duration` → [`chrono::Duration`]; `double ProgressPct` → [`f64`].
//! The C# `ConcurrentDictionary<string, T>` collapses to `Mutex`-guarded
//! `HashMap`s; `LessonsFor` reproduces the .NET `OrderBy(l => l.OrderIndex)`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::Duration;

/// (3.3.0) A course.
///
/// Mirrors `sealed record Course(string CourseId, string Name, string Subject,
/// string GradeBand)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Course {
    pub course_id: String,
    pub name: String,
    pub subject: String,
    pub grade_band: String,
}

impl Course {
    /// Constructs a course, mirroring the positional C# record constructor.
    pub fn new(
        course_id: impl Into<String>,
        name: impl Into<String>,
        subject: impl Into<String>,
        grade_band: impl Into<String>,
    ) -> Self {
        Self {
            course_id: course_id.into(),
            name: name.into(),
            subject: subject.into(),
            grade_band: grade_band.into(),
        }
    }
}

/// (3.3.0) A lesson within a course.
///
/// Mirrors `sealed record Lesson(string LessonId, string CourseId, string Title,
/// TimeSpan Duration, int OrderIndex)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Lesson {
    pub lesson_id: String,
    pub course_id: String,
    pub title: String,
    pub duration: Duration,
    pub order_index: i32,
}

impl Lesson {
    /// Constructs a lesson, mirroring the positional C# record constructor.
    pub fn new(
        lesson_id: impl Into<String>,
        course_id: impl Into<String>,
        title: impl Into<String>,
        duration: Duration,
        order_index: i32,
    ) -> Self {
        Self {
            lesson_id: lesson_id.into(),
            course_id: course_id.into(),
            title: title.into(),
            duration,
            order_index,
        }
    }
}

/// (3.3.0) A student's enrolment record.
///
/// Mirrors `sealed record StudentRecord(string StudentId, string Name,
/// string CourseId, double ProgressPct)`.
#[derive(Debug, Clone, PartialEq)]
pub struct StudentRecord {
    pub student_id: String,
    pub name: String,
    pub course_id: String,
    pub progress_pct: f64,
}

impl StudentRecord {
    /// Constructs a student record, mirroring the positional C# record constructor.
    pub fn new(
        student_id: impl Into<String>,
        name: impl Into<String>,
        course_id: impl Into<String>,
        progress_pct: f64,
    ) -> Self {
        Self {
            student_id: student_id.into(),
            name: name.into(),
            course_id: course_id.into(),
            progress_pct,
        }
    }
}

/// (3.3.0) The Education board contract.
///
/// Mirrors `interface IEducationBoard`.
pub trait IEducationBoard {
    /// Adds (or overwrites) a course.
    fn add_course(&self, c: Course);
    /// Looks up a course by id.
    fn get_course(&self, id: &str) -> Option<Course>;
    /// Adds (or overwrites) a lesson.
    fn add_lesson(&self, l: Lesson);
    /// Lessons for a course, ordered by `order_index` ascending.
    fn lessons_for(&self, course_id: &str) -> Vec<Lesson>;
    /// Enrols (or overwrites) a student.
    fn enrol(&self, r: StudentRecord);
    /// Updates a student's progress. Panics on an unknown id (C#
    /// `InvalidOperationException`).
    fn update_progress(&self, student_id: &str, pct: f64);
    /// Students enrolled in a course.
    fn students_for(&self, course_id: &str) -> Vec<StudentRecord>;
    /// Average progress across a course's students (`0.0` when empty).
    fn avg_progress_for(&self, course_id: &str) -> f64;
}

/// (3.3.0) In-memory [`IEducationBoard`].
pub struct InMemoryEducationBoard {
    courses: Mutex<HashMap<String, Course>>,
    lessons: Mutex<HashMap<String, Lesson>>,
    students: Mutex<HashMap<String, StudentRecord>>,
}

impl InMemoryEducationBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            courses: Mutex::new(HashMap::new()),
            lessons: Mutex::new(HashMap::new()),
            students: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryEducationBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IEducationBoard for InMemoryEducationBoard {
    fn add_course(&self, c: Course) {
        self.courses.lock().unwrap().insert(c.course_id.clone(), c);
    }

    fn get_course(&self, id: &str) -> Option<Course> {
        self.courses.lock().unwrap().get(id).cloned()
    }

    fn add_lesson(&self, l: Lesson) {
        self.lessons.lock().unwrap().insert(l.lesson_id.clone(), l);
    }

    fn lessons_for(&self, course_id: &str) -> Vec<Lesson> {
        let mut out: Vec<Lesson> = self
            .lessons
            .lock()
            .unwrap()
            .values()
            .filter(|l| l.course_id == course_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.order_index.cmp(&b.order_index));
        out
    }

    fn enrol(&self, r: StudentRecord) {
        self.students
            .lock()
            .unwrap()
            .insert(r.student_id.clone(), r);
    }

    fn update_progress(&self, student_id: &str, pct: f64) {
        let mut students = self.students.lock().unwrap();
        match students.get(student_id) {
            Some(r) => {
                let updated = StudentRecord {
                    progress_pct: pct,
                    ..r.clone()
                };
                students.insert(student_id.to_string(), updated);
            }
            None => panic!("Unknown student {student_id}"),
        }
    }

    fn students_for(&self, course_id: &str) -> Vec<StudentRecord> {
        self.students
            .lock()
            .unwrap()
            .values()
            .filter(|s| s.course_id == course_id)
            .cloned()
            .collect()
    }

    fn avg_progress_for(&self, course_id: &str) -> f64 {
        let rows = self.students_for(course_id);
        if rows.is_empty() {
            0.0
        } else {
            rows.iter().map(|r| r.progress_pct).sum::<f64>() / rows.len() as f64
        }
    }
}
