# education_primitives.py
#
# Port of CircleAI.Education EducationPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Education vertical:
# courses, lessons, student records + progress.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a single lock.
# C# TimeSpan Duration maps to datetime.timedelta. C# OrderBy is stable, as is
# Python's sorted(). AvgProgressFor returns 0.0 (not NaN) on an empty course,
# mirroring the C# `rows.Count == 0 ? 0.0 : rows.Average(...)`.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import timedelta
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Course:
    """Mirrors ``CircleAI.Education.Course`` —
    ``record(string CourseId, string Name, string Subject, string GradeBand)``.
    """

    course_id: str
    name: str
    subject: str
    grade_band: str


@dataclass(frozen=True, slots=True)
class Lesson:
    """Mirrors ``CircleAI.Education.Lesson`` — ``record(string LessonId,
    string CourseId, string Title, TimeSpan Duration, int OrderIndex)``.
    """

    lesson_id: str
    course_id: str
    title: str
    duration: timedelta
    order_index: int


@dataclass(frozen=True, slots=True)
class StudentRecord:
    """Mirrors ``CircleAI.Education.StudentRecord`` — ``record(string StudentId,
    string Name, string CourseId, double ProgressPct)``.
    """

    student_id: str
    name: str
    course_id: str
    progress_pct: float


class IEducationBoard(ABC):
    """In-memory board for courses, lessons and student progress."""

    @abstractmethod
    def add_course(self, c: Course) -> None:
        ...

    @abstractmethod
    def get_course(self, id: str) -> Optional[Course]:
        ...

    @abstractmethod
    def add_lesson(self, l: Lesson) -> None:
        ...

    @abstractmethod
    def lessons_for(self, course_id: str) -> List[Lesson]:
        ...

    @abstractmethod
    def enrol(self, r: StudentRecord) -> None:
        ...

    @abstractmethod
    def update_progress(self, student_id: str, pct: float) -> None:
        ...

    @abstractmethod
    def students_for(self, course_id: str) -> List[StudentRecord]:
        ...

    @abstractmethod
    def avg_progress_for(self, course_id: str) -> float:
        ...


class InMemoryEducationBoard(IEducationBoard):
    """Thread-safe in-memory :class:`IEducationBoard`."""

    def __init__(self) -> None:
        self._courses: Dict[str, Course] = {}
        self._lessons: Dict[str, Lesson] = {}
        self._students: Dict[str, StudentRecord] = {}
        self._lock = threading.Lock()

    def add_course(self, c: Course) -> None:
        if c is None:
            raise ValueError("course must not be None")
        with self._lock:
            self._courses[c.course_id] = c

    def get_course(self, id: str) -> Optional[Course]:
        with self._lock:
            return self._courses.get(id)

    def add_lesson(self, l: Lesson) -> None:
        if l is None:
            raise ValueError("lesson must not be None")
        with self._lock:
            self._lessons[l.lesson_id] = l

    def lessons_for(self, course_id: str) -> List[Lesson]:
        with self._lock:
            rows = [l for l in self._lessons.values() if l.course_id == course_id]
        return sorted(rows, key=lambda l: l.order_index)

    def enrol(self, r: StudentRecord) -> None:
        if r is None:
            raise ValueError("student record must not be None")
        with self._lock:
            self._students[r.student_id] = r

    def update_progress(self, student_id: str, pct: float) -> None:
        with self._lock:
            r = self._students.get(student_id)
            if r is None:
                raise RuntimeError(f"Unknown student {student_id}")
            self._students[student_id] = replace(r, progress_pct=pct)

    def students_for(self, course_id: str) -> List[StudentRecord]:
        with self._lock:
            return [s for s in self._students.values() if s.course_id == course_id]

    def avg_progress_for(self, course_id: str) -> float:
        rows = self.students_for(course_id)
        if len(rows) == 0:
            return 0.0
        return sum(r.progress_pct for r in rows) / len(rows)
