// Education.swift
//
// Port of the Education vertical from src/CircleAI.Education/EducationPrimitives.cs
// and the static domain-context constants from EducationDomainContext.cs:
//   • Course, Lesson, StudentRecord — domain records
//   • IEducationBoard               — courses / lessons / enrolment / progress
//   • InMemoryEducationBoard        — deterministic in-memory impl
//   • EducationDomainContext        — system-prompt snippet + flags
//
// The Companion-facing wrapper (EducationCompanionAdapter) is intentionally NOT
// ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `TimeSpan` (Lesson.duration) → `TimeInterval` (seconds), Codable as a
//     Double.
//   • `UpdateProgress` on an unknown student throws → `EducationError.unknownStudent`.
//   • `LessonsFor` orders ascending by orderIndex (stable). `StudentsFor` is
//     dictionary-values order (unordered); `AvgProgressFor` returns 0.0 for an
//     empty course, otherwise the arithmetic mean of `progressPct`.

import Foundation

// MARK: - Records

/// A course of study.
public struct Course: Sendable, Equatable, Codable {
    /// Stable identifier for the course.
    public let courseId: String
    /// Course name.
    public let name: String
    /// Subject area.
    public let subject: String
    /// Grade band (e.g. "Grade 8-9").
    public let gradeBand: String

    public init(courseId: String, name: String, subject: String, gradeBand: String) {
        self.courseId = courseId
        self.name = name
        self.subject = subject
        self.gradeBand = gradeBand
    }
}

/// A lesson within a course.
public struct Lesson: Sendable, Equatable, Codable {
    /// Stable identifier for the lesson.
    public let lessonId: String
    /// Identifier of the owning course.
    public let courseId: String
    /// Lesson title.
    public let title: String
    /// Lesson duration in seconds.
    public let duration: TimeInterval
    /// Position of the lesson within the course (ascending).
    public let orderIndex: Int

    public init(lessonId: String, courseId: String, title: String, duration: TimeInterval, orderIndex: Int) {
        self.lessonId = lessonId
        self.courseId = courseId
        self.title = title
        self.duration = duration
        self.orderIndex = orderIndex
    }
}

/// A student's enrolment + progress in a course.
public struct StudentRecord: Sendable, Equatable, Codable {
    /// Stable identifier for the student.
    public let studentId: String
    /// Student's name.
    public let name: String
    /// Identifier of the enrolled course.
    public let courseId: String
    /// Progress through the course, 0–100.
    public let progressPct: Double

    public init(studentId: String, name: String, courseId: String, progressPct: Double) {
        self.studentId = studentId
        self.name = name
        self.courseId = courseId
        self.progressPct = progressPct
    }
}

// MARK: - Errors

/// Errors thrown by the education board.
public enum EducationError: Error, Equatable, CustomStringConvertible {
    /// `updateProgress` referenced a student id that is not known.
    case unknownStudent(String)

    public var description: String {
        switch self {
        case .unknownStudent(let id): return "Unknown student \(id)"
        }
    }
}

// MARK: - IEducationBoard

/// Courses, lessons, enrolment, and progress tracking for the education
/// vertical. A synchronous contract — implementations are expected to be
/// thread-safe.
public protocol IEducationBoard: AnyObject, Sendable {
    /// Adds (or replaces, by `courseId`) a course.
    func addCourse(_ c: Course)
    /// Returns the course with `id`, or `nil`.
    func getCourse(_ id: String) -> Course?
    /// Adds (or replaces, by `lessonId`) a lesson.
    func addLesson(_ l: Lesson)
    /// Lessons for `courseId`, ordered ascending by `orderIndex`.
    func lessonsFor(courseId: String) -> [Lesson]
    /// Enrols (or replaces, by `studentId`) a student.
    func enrol(_ r: StudentRecord)
    /// Updates a student's progress. Throws when the student is unknown.
    func updateProgress(studentId: String, pct: Double) throws
    /// Students enrolled in `courseId`.
    func studentsFor(courseId: String) -> [StudentRecord]
    /// Mean progress across students in `courseId` (0.0 if none).
    func avgProgressFor(courseId: String) -> Double
}

// MARK: - InMemoryEducationBoard

/// Deterministic in-memory `IEducationBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryEducationBoard: IEducationBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var courses: [String: Course] = [:]
    private var lessons: [String: Lesson] = [:]
    private var students: [String: StudentRecord] = [:]

    public init() {}

    public func addCourse(_ c: Course) {
        lock.lock(); defer { lock.unlock() }
        courses[c.courseId] = c
    }

    public func getCourse(_ id: String) -> Course? {
        lock.lock(); defer { lock.unlock() }
        return courses[id]
    }

    public func addLesson(_ l: Lesson) {
        lock.lock(); defer { lock.unlock() }
        lessons[l.lessonId] = l
    }

    public func lessonsFor(courseId: String) -> [Lesson] {
        lock.lock(); defer { lock.unlock() }
        return lessons.values.filter { $0.courseId == courseId }.sorted { $0.orderIndex < $1.orderIndex }
    }

    public func enrol(_ r: StudentRecord) {
        lock.lock(); defer { lock.unlock() }
        students[r.studentId] = r
    }

    public func updateProgress(studentId: String, pct: Double) throws {
        lock.lock(); defer { lock.unlock() }
        guard let r = students[studentId] else { throw EducationError.unknownStudent(studentId) }
        students[studentId] = StudentRecord(studentId: r.studentId, name: r.name,
                                            courseId: r.courseId, progressPct: pct)
    }

    public func studentsFor(courseId: String) -> [StudentRecord] {
        lock.lock(); defer { lock.unlock() }
        return students.values.filter { $0.courseId == courseId }
    }

    public func avgProgressFor(courseId: String) -> Double {
        let rows = studentsFor(courseId: courseId)
        return rows.isEmpty ? 0.0 : rows.reduce(0.0) { $0 + $1.progressPct } / Double(rows.count)
    }
}

// MARK: - EducationDomainContext

/// Static domain-context constants for the education vertical. Mirrors
/// `EducationDomainContext` in EducationDomainContext.cs.
public enum EducationDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Education] Expert education assistant. Help with lesson plan design, curriculum alignment (CAPS/NCS), assessment rubric creation, differentiated instruction strategies, and learner progress tracking. Adapt communication to the relevant grade level and learning style. Compliance: SASA, DBE curriculum frameworks, POPIA for learner data."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["SASA", "CAPS_NCS", "POPIA", "PAIA"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["learning_management", "document_editor", "assessment_tools", "web_search"]
}
