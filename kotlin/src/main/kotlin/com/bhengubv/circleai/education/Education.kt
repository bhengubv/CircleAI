// Education.kt
//
// Kotlin port of CircleAI.Education (EducationPrimitives.cs +
// EducationDomainContext.cs + EducationCompanionAdapter.cs) — the C# reference
// is the EXACT spec. Deterministic in-memory education board: courses,
// lessons, and student progress.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `TimeSpan` (Lesson.Duration) -> `java.time.Duration`.
//   * C# `double` (ProgressPct) -> Kotlin `Double`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * `LessonsFor` orders by OrderIndex ASC.
//   * `UpdateProgress` on an unknown student throws.
//   * `AvgProgressFor` returns 0.0 for an empty cohort, else the mean progress.

package com.bhengubv.circleai.education

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (EducationPrimitives.cs)
// =====================================================================

/** A course. Mirrors C# `Course`. */
data class Course(val courseId: String, val name: String, val subject: String, val gradeBand: String)

/** A lesson within a course. Mirrors C# `Lesson`. */
data class Lesson(
    val lessonId: String,
    val courseId: String,
    val title: String,
    val duration: Duration,
    val orderIndex: Int,
)

/** A student's enrolment + progress record. Mirrors C# `StudentRecord`. */
data class StudentRecord(val studentId: String, val name: String, val courseId: String, val progressPct: Double)

/** Deterministic education board. Mirrors C# `IEducationBoard`. */
interface IEducationBoard {
    fun addCourse(c: Course)
    fun getCourse(id: String): Course?
    fun addLesson(l: Lesson)
    fun lessonsFor(courseId: String): List<Lesson>
    fun enrol(r: StudentRecord)
    fun updateProgress(studentId: String, pct: Double)
    fun studentsFor(courseId: String): List<StudentRecord>
    fun avgProgressFor(courseId: String): Double
}

/** In-memory [IEducationBoard]. Mirrors C# `InMemoryEducationBoard`. */
class InMemoryEducationBoard : IEducationBoard {
    private val courses = ConcurrentHashMap<String, Course>()
    private val lessons = ConcurrentHashMap<String, Lesson>()
    private val students = ConcurrentHashMap<String, StudentRecord>()

    override fun addCourse(c: Course) { courses[c.courseId] = c }
    override fun getCourse(id: String): Course? = courses[id]
    override fun addLesson(l: Lesson) { lessons[l.lessonId] = l }

    override fun lessonsFor(courseId: String): List<Lesson> =
        lessons.values.filter { it.courseId == courseId }.sortedBy { it.orderIndex }

    override fun enrol(r: StudentRecord) { students[r.studentId] = r }

    override fun updateProgress(studentId: String, pct: Double) {
        val r = students[studentId] ?: throw IllegalStateException("Unknown student $studentId")
        students[studentId] = r.copy(progressPct = pct)
    }

    override fun studentsFor(courseId: String): List<StudentRecord> =
        students.values.filter { it.courseId == courseId }

    override fun avgProgressFor(courseId: String): Double {
        val rows = studentsFor(courseId)
        return if (rows.isEmpty()) 0.0 else rows.map { it.progressPct }.average()
    }
}

// =====================================================================
// DomainContext (EducationDomainContext.cs)
// =====================================================================

/** Static domain context for the Education vertical. Mirrors C# `EducationDomainContext`. */
object EducationDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Education] Expert education assistant. Help with lesson plan design, curriculum " +
            "alignment (CAPS/NCS), assessment rubric creation, differentiated instruction strategies, " +
            "and learner progress tracking. Adapt communication to the relevant grade level and learning " +
            "style. Compliance: SASA, DBE curriculum frameworks, POPIA for learner data."

    val complianceFlags: List<String> = listOf("SASA", "CAPS_NCS", "POPIA", "PAIA")

    val suggestedTools: List<String> =
        listOf("learning_management", "document_editor", "assessment_tools", "web_search")
}

// =====================================================================
// CompanionAdapter (EducationCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Education domain snippet + domain
 * helpers. Mirrors C# `EducationCompanionAdapter`.
 */
class EducationCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)
    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrich(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrich(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrich(instruction))

    private fun enrich(m: String): String = "${EducationDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun createLessonPlanAsync(subject: String, grade: String, topic: String, duration: String): String =
        inner.agentAsync("Create a CAPS-aligned lesson plan for Grade $grade $subject: $topic. Duration: $duration. Include LTSM, activities, differentiation strategies, and assessment criteria.")

    suspend fun generateRubricAsync(assessmentTask: String, grade: String): String =
        inner.agentAsync("Generate an assessment rubric for Grade $grade: $assessmentTask. Include criteria, descriptors for 4 performance levels, and weighting.")

    suspend fun designLessonPlanAsync(topic: String, gradeBand: String, minutes: Int): String =
        inner.agentAsync("Design a $minutes-minute lesson plan on '$topic' for $gradeBand. Include objectives, hook, instruction, practice, exit ticket.")

    suspend fun generateAssessmentAsync(topic: String, bloomsLevel: String, itemCount: Int): String =
        inner.agentAsync("Generate $itemCount assessment items on '$topic' at Bloom's $bloomsLevel level. Mix MCQ + short-answer + one performance task.")

    suspend fun diagnoseMisconceptionAsync(topic: String, studentResponse: String): String =
        inner.agentAsync("Diagnose the misconception in this student response on '$topic': $studentResponse. Identify the rule the student is following + a corrective move.")

    suspend fun draftParentUpdateAsync(studentName: String, period: String, progressNotes: String): String =
        inner.agentAsync("Draft a parent update for $studentName covering $period. Notes: $progressNotes. Warm, specific, actionable.")
}
