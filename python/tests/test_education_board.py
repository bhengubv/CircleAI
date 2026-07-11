"""test_education_board.py — CircleAI.Education port.

Covers the domain records, InMemoryEducationBoard (course upsert, lesson ordering
by OrderIndex, enrolment + progress update, students-for filtering, average
progress with the empty-course 0.0 rule) and the static EducationDomainContext.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import timedelta

import pytest

from circle_ai import (
    Course,
    EducationDomainContext,
    IEducationBoard,
    InMemoryEducationBoard,
    Lesson,
    StudentRecord,
)


def test_board_is_ieducationboard():
    assert isinstance(InMemoryEducationBoard(), IEducationBoard)


def test_add_and_get_course_upserts():
    board = InMemoryEducationBoard()
    assert board.get_course("c1") is None
    board.add_course(Course("c1", "Maths", "STEM", "Grade 8"))
    board.add_course(Course("c1", "Maths II", "STEM", "Grade 9"))
    got = board.get_course("c1")
    assert got is not None and got.name == "Maths II" and got.grade_band == "Grade 9"


def test_add_course_none_raises():
    with pytest.raises(ValueError):
        InMemoryEducationBoard().add_course(None)  # type: ignore[arg-type]


def test_lessons_for_orders_by_order_index():
    board = InMemoryEducationBoard()
    board.add_lesson(Lesson("l3", "c1", "C", timedelta(minutes=45), 3))
    board.add_lesson(Lesson("l1", "c1", "A", timedelta(minutes=30), 1))
    board.add_lesson(Lesson("l2", "c1", "B", timedelta(minutes=60), 2))
    board.add_lesson(Lesson("lx", "other", "X", timedelta(minutes=10), 1))
    lessons = board.lessons_for("c1")
    assert [l.lesson_id for l in lessons] == ["l1", "l2", "l3"]
    assert all(l.course_id == "c1" for l in lessons)


def test_add_lesson_none_raises():
    with pytest.raises(ValueError):
        InMemoryEducationBoard().add_lesson(None)  # type: ignore[arg-type]


def test_enrol_update_progress_and_students_for():
    board = InMemoryEducationBoard()
    board.enrol(StudentRecord("s1", "Ann", "c1", 10.0))
    board.enrol(StudentRecord("s2", "Ben", "c1", 20.0))
    board.enrol(StudentRecord("s3", "Cid", "other", 99.0))
    board.update_progress("s1", 50.0)
    students = {s.student_id: s for s in board.students_for("c1")}
    assert set(students) == {"s1", "s2"}
    assert students["s1"].progress_pct == 50.0


def test_update_progress_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryEducationBoard().update_progress("nope", 1.0)


def test_enrol_none_raises():
    with pytest.raises(ValueError):
        InMemoryEducationBoard().enrol(None)  # type: ignore[arg-type]


def test_avg_progress_for_computes_mean():
    board = InMemoryEducationBoard()
    board.enrol(StudentRecord("s1", "Ann", "c1", 40.0))
    board.enrol(StudentRecord("s2", "Ben", "c1", 60.0))
    assert board.avg_progress_for("c1") == pytest.approx(50.0)


def test_avg_progress_for_empty_course_is_zero():
    board = InMemoryEducationBoard()
    assert board.avg_progress_for("nobody") == 0.0


def test_education_domain_context():
    assert EducationDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Education]")
    assert "CAPS/NCS" in EducationDomainContext.SystemPromptSnippet
    assert list(EducationDomainContext.ComplianceFlags) == ["SASA", "CAPS_NCS", "POPIA", "PAIA"]
    assert list(EducationDomainContext.SuggestedTools) == [
        "learning_management",
        "document_editor",
        "assessment_tools",
        "web_search",
    ]
