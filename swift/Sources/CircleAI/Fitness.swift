// Fitness.swift
//
// Port of the Fitness vertical from src/CircleAI.Fitness/FitnessPrimitives.cs
// and the static domain-context constants from FitnessDomainContext.cs:
//   • Workout, FitnessGoal, ExerciseSet — domain records
//   • IFitnessBoard                     — workouts, calories, goals, sets
//   • InMemoryFitnessBoard              — deterministic in-memory impl
//   • FitnessDomainContext              — system-prompt snippet + flags
//
// The Companion-facing wrapper (FitnessCompanionAdapter) is an
// ICompanionSession decorator that prefixes the fitness domain prompt.
//
// Porting notes:
//   • `DateTimeOffset`/`DateTime` → `Date`.
//   • `WorkoutsThisWeek` filters user + AtUtc >= week start (Sunday = 0),
//     ordered ascending by AtUtc.
//   • `TotalCaloriesSince` sums CaloriesBurned for user since `since`.
//   • `GoalsFor` filters by UserId (unordered). `SetsFor` filters by WorkoutId.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A logged workout.
public struct Workout: Sendable, Equatable, Codable {
    public let workoutId: String
    public let userId: String
    public let kind: String
    public let durationMinutes: Int
    public let caloriesBurned: Double
    public let atUtc: Date

    public init(workoutId: String, userId: String, kind: String, durationMinutes: Int, caloriesBurned: Double, atUtc: Date) {
        self.workoutId = workoutId
        self.userId = userId
        self.kind = kind
        self.durationMinutes = durationMinutes
        self.caloriesBurned = caloriesBurned
        self.atUtc = atUtc
    }
}

/// A fitness goal with a target metric and due date.
public struct FitnessGoal: Sendable, Equatable, Codable {
    public let goalId: String
    public let userId: String
    public let metric: String
    public let target: Double
    public let dueOn: Date

    public init(goalId: String, userId: String, metric: String, target: Double, dueOn: Date) {
        self.goalId = goalId
        self.userId = userId
        self.metric = metric
        self.target = target
        self.dueOn = dueOn
    }
}

/// A single exercise set within a workout.
public struct ExerciseSet: Sendable, Equatable, Codable {
    public let setId: String
    public let workoutId: String
    public let exercise: String
    public let reps: Int
    public let weightKg: Double

    public init(setId: String, workoutId: String, exercise: String, reps: Int, weightKg: Double) {
        self.setId = setId
        self.workoutId = workoutId
        self.exercise = exercise
        self.reps = reps
        self.weightKg = weightKg
    }
}

// MARK: - Contract

/// Workouts, calorie totals, goals, and exercise sets for the fitness vertical.
public protocol IFitnessBoard: AnyObject, Sendable {
    func log(_ w: Workout)
    func workoutsThisWeek(userId: String, now: Date) -> [Workout]
    func totalCaloriesSince(userId: String, since: Date) -> Double
    func setGoal(_ g: FitnessGoal)
    func goalsFor(userId: String) -> [FitnessGoal]
    func addSet(_ s: ExerciseSet)
    func setsFor(workoutId: String) -> [ExerciseSet]
}

// MARK: - InMemoryFitnessBoard

/// Deterministic in-memory `IFitnessBoard`. All state guarded by a single `NSLock`.
public final class InMemoryFitnessBoard: IFitnessBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var workouts: [Workout] = []
    private var goals: [String: FitnessGoal] = [:]
    private var sets: [ExerciseSet] = []

    public init() {}

    public func log(_ w: Workout) {
        lock.lock(); defer { lock.unlock() }
        workouts.append(w)
    }

    public func workoutsThisWeek(userId: String, now: Date) -> [Workout] {
        let weekStart = Self.weekStart(now)
        lock.lock(); defer { lock.unlock() }
        return workouts.filter { $0.userId == userId && $0.atUtc >= weekStart }.sorted { $0.atUtc < $1.atUtc }
    }

    public func totalCaloriesSince(userId: String, since: Date) -> Double {
        lock.lock(); defer { lock.unlock() }
        return workouts.filter { $0.userId == userId && $0.atUtc >= since }.reduce(0.0) { $0 + $1.caloriesBurned }
    }

    public func setGoal(_ g: FitnessGoal) {
        lock.lock(); defer { lock.unlock() }
        goals[g.goalId] = g
    }

    public func goalsFor(userId: String) -> [FitnessGoal] {
        lock.lock(); defer { lock.unlock() }
        return goals.values.filter { $0.userId == userId }
    }

    public func addSet(_ s: ExerciseSet) {
        lock.lock(); defer { lock.unlock() }
        sets.append(s)
    }

    public func setsFor(workoutId: String) -> [ExerciseSet] {
        lock.lock(); defer { lock.unlock() }
        return sets.filter { $0.workoutId == workoutId }
    }

    /// Total number of logged workouts (matches C#'s `WorkoutCount`).
    public var workoutCount: Int {
        lock.lock(); defer { lock.unlock() }
        return workouts.count
    }

    /// A user's workouts of a given kind (userId exact, kind case-insensitive),
    /// newest first. Matches C#'s `WorkoutsByKind` → `OrderByDescending(AtUtc)`.
    public func workoutsByKind(userId: String, kind: String) -> [Workout] {
        lock.lock(); defer { lock.unlock() }
        return workouts
            .filter { $0.userId == userId && $0.kind.caseInsensitiveCompare(kind) == .orderedSame }
            .sorted { $0.atUtc > $1.atUtc }
    }

    /// Remove a goal by id. Returns true if present (matches C#'s `RemoveGoal` →
    /// `TryRemove`).
    @discardableResult
    public func removeGoal(_ goalId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return goals.removeValue(forKey: goalId) != nil
    }

    /// The user's soonest-due goal for a metric (userId exact, metric
    /// case-insensitive), or nil. Matches C#'s `GoalByMetric` →
    /// `OrderBy(DueOn).FirstOrDefault()`.
    public func goalByMetric(userId: String, metric: String) -> FitnessGoal? {
        lock.lock(); defer { lock.unlock() }
        return goals.values
            .filter { $0.userId == userId && $0.metric.caseInsensitiveCompare(metric) == .orderedSame }
            .sorted { $0.dueOn < $1.dueOn }
            .first
    }

    /// Mean workout duration (minutes) for a user since `since`. Empty → 0
    /// (matches C#'s `AvgDurationSince` → `DefaultIfEmpty(0).Average()`).
    public func avgDurationSince(userId: String, since: Date) -> Double {
        lock.lock(); defer { lock.unlock() }
        let durations = workouts
            .filter { $0.userId == userId && $0.atUtc >= since }
            .map { Double($0.durationMinutes) }
        guard !durations.isEmpty else { return 0 }
        return durations.reduce(0, +) / Double(durations.count)
    }

    /// Total lifted volume (kg) for a workout: Σ reps × weightKg. Matches C#'s
    /// `TotalVolumeKg`.
    public func totalVolumeKg(_ workoutId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        return sets
            .filter { $0.workoutId == workoutId }
            .reduce(0.0) { $0 + Double($1.reps) * $1.weightKg }
    }

    private static func weekStart(_ now: Date) -> Date {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let startOfDay = cal.startOfDay(for: now)
        let weekdayIndex = cal.component(.weekday, from: startOfDay) - 1
        return cal.date(byAdding: .day, value: -weekdayIndex, to: startOfDay)!
    }
}

// MARK: - FitnessDomainContext

/// Static domain-context constants for the fitness vertical.
public enum FitnessDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Fitness] Personal fitness coach companion. Help with training programme design, workout planning, recovery protocols, nutritional timing, and progress analysis. Apply evidence-based exercise science principles. Not a medical service. Compliance: HPCSA fitness guidelines, POPIA."
    public static let complianceFlags: [String] = ["HPCSA_Fitness", "POPIA", "Not_Medical_Advice"]
    public static let suggestedTools: [String] = ["fitness_tracker", "exercise_db", "nutrition_tools", "analytics"]
}

// MARK: - FitnessCompanionAdapter

/// An `ICompanionSession` decorator that prepends the fitness domain system
/// prompt to every conversational call and adds fitness helper methods.
/// Port of `CircleAI.Fitness.FitnessCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class FitnessCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(FitnessDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Fitness helpers ───────────────────────────────────────────────────────

    /// Design a workout programme (C# `DesignWorkoutAsync`).
    public func designWorkout(goal: String, equipment: String, level: String, daysPerWeek: Int) async throws -> String {
        try await inner.agent(
            "Design a \(daysPerWeek)-day/week workout programme. Goal: \(goal). Equipment: \(equipment). Level: \(level). Include warm-up, main sets with reps/sets/rest, and cool-down.")
    }

    /// Analyse fitness progress (C# `AnalyseProgressAsync`).
    public func analyseProgress(metrics: String) async throws -> String {
        try await inner.agent("Analyse my fitness progress and recommend programme adjustments:\n\(metrics)")
    }

    /// Design a periodised workout plan (C# `DesignWorkoutPlanAsync`).
    public func designWorkoutPlan(goal: String, availableTime: String, equipment: String) async throws -> String {
        try await inner.agent(
            "Design a workout plan for goal '\(goal)', \(availableTime) per session, equipment: \(equipment). Periodise over 4 weeks.")
    }

    /// Analyse personal-best progression (C# `AnalysePersonalBestProgressionAsync`).
    public func analysePersonalBestProgression(exercise: String, historyJson: String) async throws -> String {
        try await inner.agent(
            "Analyse PB progression in \(exercise): \(historyJson). Identify plateaus, recommend deload + next mesocycle target.")
    }

    /// Suggest a recovery protocol (C# `SuggestRecoveryProtocolAsync`).
    public func suggestRecoveryProtocol(sorenessNotes: String, sleepAvgHours: String) async throws -> String {
        try await inner.agent(
            "Suggest recovery protocol for soreness: \(sorenessNotes), avg sleep \(sleepAvgHours)h. Cover mobility, nutrition, sleep, deload.")
    }

    /// Critique a form cue (C# `CritiqueFormCueAsync`).
    public func critiqueFormCue(exercise: String, formDescription: String) async throws -> String {
        try await inner.agent(
            "Critique form for \(exercise): \(formDescription). Identify the 2 highest-leverage cues to fix first.")
    }
}
