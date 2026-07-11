// EducationBoardTests.swift
//
// Exercises the education records' Codable round-trips and the deterministic
// behaviour of InMemoryEducationBoard — course/lesson management with
// order-index sorting, enrolment + progress updates (incl. unknown-student
// throw), and average-progress (incl. the empty-course 0.0 case). Mirrors
// CircleAI.Education/EducationPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class EducationBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testCourseCodableRoundTrip() throws {
        let c = Course(courseId: "c1", name: "Algebra", subject: "Maths", gradeBand: "8-9")
        XCTAssertEqual(try JSONDecoder().decode(Course.self, from: try JSONEncoder().encode(c)), c)
    }

    func testLessonCodableRoundTrip() throws {
        let l = Lesson(lessonId: "l1", courseId: "c1", title: "Intro", duration: 45 * 60, orderIndex: 1)
        XCTAssertEqual(try JSONDecoder().decode(Lesson.self, from: try JSONEncoder().encode(l)), l)
    }

    func testStudentRecordCodableRoundTrip() throws {
        let s = StudentRecord(studentId: "s1", name: "Bo", courseId: "c1", progressPct: 42.5)
        XCTAssertEqual(try JSONDecoder().decode(StudentRecord.self, from: try JSONEncoder().encode(s)), s)
    }

    // ── Courses & lessons ────────────────────────────────────────────────────

    func testAddAndGetCourse() {
        let b = InMemoryEducationBoard()
        b.addCourse(Course(courseId: "c1", name: "Algebra", subject: "Maths", gradeBand: "8"))
        XCTAssertEqual(b.getCourse("c1")?.name, "Algebra")
        XCTAssertNil(b.getCourse("missing"))
    }

    func testLessonsForOrderedByOrderIndex() {
        let b = InMemoryEducationBoard()
        b.addLesson(Lesson(lessonId: "l3", courseId: "c1", title: "C", duration: 60, orderIndex: 3))
        b.addLesson(Lesson(lessonId: "l1", courseId: "c1", title: "A", duration: 60, orderIndex: 1))
        b.addLesson(Lesson(lessonId: "l2", courseId: "c1", title: "B", duration: 60, orderIndex: 2))
        b.addLesson(Lesson(lessonId: "lx", courseId: "other", title: "X", duration: 60, orderIndex: 0))
        XCTAssertEqual(b.lessonsFor(courseId: "c1").map { $0.lessonId }, ["l1", "l2", "l3"])
    }

    // ── Enrolment & progress ─────────────────────────────────────────────────

    func testEnrolUpdateProgressAndStudentsFor() throws {
        let b = InMemoryEducationBoard()
        b.enrol(StudentRecord(studentId: "s1", name: "A", courseId: "c1", progressPct: 0))
        b.enrol(StudentRecord(studentId: "s2", name: "B", courseId: "c1", progressPct: 0))
        b.enrol(StudentRecord(studentId: "s3", name: "C", courseId: "other", progressPct: 0))
        try b.updateProgress(studentId: "s1", pct: 80)
        XCTAssertEqual(Set(b.studentsFor(courseId: "c1").map { $0.studentId }), ["s1", "s2"])
        let s1 = b.studentsFor(courseId: "c1").first { $0.studentId == "s1" }
        XCTAssertEqual(s1?.progressPct, 80)
    }

    func testUpdateProgressThrowsForUnknownStudent() {
        let b = InMemoryEducationBoard()
        XCTAssertThrowsError(try b.updateProgress(studentId: "ghost", pct: 10)) { error in
            XCTAssertEqual(error as? EducationError, .unknownStudent("ghost"))
        }
    }

    // ── Average progress ─────────────────────────────────────────────────────

    func testAvgProgressForComputesMean() throws {
        let b = InMemoryEducationBoard()
        b.enrol(StudentRecord(studentId: "s1", name: "A", courseId: "c1", progressPct: 40))
        b.enrol(StudentRecord(studentId: "s2", name: "B", courseId: "c1", progressPct: 60))
        XCTAssertEqual(b.avgProgressFor(courseId: "c1"), 50, accuracy: 1e-9)
    }

    func testAvgProgressForEmptyCourseIsZero() {
        XCTAssertEqual(InMemoryEducationBoard().avgProgressFor(courseId: "nobody"), 0.0)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(EducationDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Education]"))
        XCTAssertTrue(EducationDomainContext.complianceFlags.contains("CAPS_NCS"))
    }
}
