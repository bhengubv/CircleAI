/**
 * education/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// education/index.ts
// Full-parity port of CircleAI.Education (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Education vertical: courses, lessons,
// student records with progress, and an average-progress rollup. Plus the
// static EducationDomainContext.
//
// Type mappings (C# → TS):
//   record            → readonly interface (+ positional factory)
//   TimeSpan Duration → number of milliseconds (a TimeSpan is a duration; only
//                       OrderIndex is used for ordering, so the ms encoding is
//                       purely a faithful carrier of the value)
//   double ProgressPct→ number
//   ConcurrentDictionary (Ordinal) → Map<string,T>
//
// ORDERING / ROLLUP PARITY:
//   LessonsFor      — sorted by OrderIndex ascending
//   AvgProgressFor  — arithmetic mean of ProgressPct, or 0.0 when no students

/** A course. Mirrors C# `Course` record. */
export interface Course {
  readonly courseId: string;
  readonly name: string;
  readonly subject: string;
  readonly gradeBand: string;
}

/** Constructs a {@link Course}. */
export function course(courseId: string, name: string, subject: string, gradeBand: string): Course {
  return { courseId, name, subject, gradeBand };
}

/** A lesson within a course. Mirrors C# `Lesson` record. */
export interface Lesson {
  readonly lessonId: string;
  readonly courseId: string;
  readonly title: string;
  /** Lesson length as a duration in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  readonly orderIndex: number;
}

/** Constructs a {@link Lesson}. */
export function lesson(
  lessonId: string,
  courseId: string,
  title: string,
  durationMs: number,
  orderIndex: number,
): Lesson {
  return { lessonId, courseId, title, durationMs, orderIndex };
}

/** A student's enrolment + progress record. Mirrors C# `StudentRecord` record. */
export interface StudentRecord {
  readonly studentId: string;
  readonly name: string;
  readonly courseId: string;
  readonly progressPct: number;
}

/** Constructs a {@link StudentRecord}. */
export function studentRecord(studentId: string, name: string, courseId: string, progressPct: number): StudentRecord {
  return { studentId, name, courseId, progressPct };
}

/** The education board contract. */
export interface IEducationBoard {
  addCourse(c: Course): void;
  getCourse(id: string): Course | undefined;
  addLesson(l: Lesson): void;
  lessonsFor(courseId: string): readonly Lesson[];
  enrol(r: StudentRecord): void;
  updateProgress(studentId: string, pct: number): void;
  studentsFor(courseId: string): readonly StudentRecord[];
  avgProgressFor(courseId: string): number;
}

/** Deterministic in-memory {@link IEducationBoard}. */
export class InMemoryEducationBoard implements IEducationBoard {
  private readonly courses = new Map<string, Course>();
  private readonly lessons = new Map<string, Lesson>();
  private readonly students = new Map<string, StudentRecord>();

  addCourse(c: Course): void {
    if (c == null) throw new Error("c required");
    this.courses.set(c.courseId, c);
  }

  getCourse(id: string): Course | undefined {
    return this.courses.get(id);
  }

  addLesson(l: Lesson): void {
    if (l == null) throw new Error("l required");
    this.lessons.set(l.lessonId, l);
  }

  lessonsFor(courseId: string): readonly Lesson[] {
    return [...this.lessons.values()]
      .filter((l) => l.courseId === courseId)
      .sort((a, b) => a.orderIndex - b.orderIndex);
  }

  enrol(r: StudentRecord): void {
    if (r == null) throw new Error("r required");
    this.students.set(r.studentId, r);
  }

  updateProgress(studentId: string, pct: number): void {
    const r = this.students.get(studentId);
    if (r === undefined) throw new Error(`Unknown student ${studentId}`);
    this.students.set(studentId, { ...r, progressPct: pct });
  }

  studentsFor(courseId: string): readonly StudentRecord[] {
    return [...this.students.values()].filter((s) => s.courseId === courseId);
  }

  avgProgressFor(courseId: string): number {
    const rows = this.studentsFor(courseId);
    if (rows.length === 0) return 0.0;
    return rows.reduce((sum, r) => sum + r.progressPct, 0) / rows.length;
  }
}

/**
 * Static domain context for the Education vertical. Mirrors C#
 * `EducationDomainContext`.
 */
export const EducationDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Education] Expert education assistant. Help with lesson plan design, curriculum alignment (CAPS/NCS), assessment rubric creation, differentiated instruction strategies, and learner progress tracking. Adapt communication to the relevant grade level and learning style. Compliance: SASA, DBE curriculum frameworks, POPIA for learner data.",
  complianceFlags: ["SASA", "CAPS_NCS", "POPIA", "PAIA"] as readonly string[],
  suggestedTools: ["learning_management", "document_editor", "assessment_tools", "web_search"] as readonly string[],
} as const;
