// Career.swift
//
// Port of src/CircleAI.Career/:
//   • CareerProfile.cs     → ProfileIdentity, ProfileSkill, ProfileHistory,
//                            ProfileEducation, ProfileCertification,
//                            ProfileLanguage, CareerProfile
//   • CareerInterview.cs   → ProfileField, InterviewQuestion, CareerInterview
//   • ProfileTailoring.cs  → TailoringChoice, ProfileTailoring
//   • ProfileToCv.cs       → ProfileToCv
//   • SqliteCareerStore.cs → JobSpec, ApprovedDocument, CareerStore (protocol)
//
// Porting notes:
//   • SqliteCareerStore does NOT port. It is Microsoft.Data.Sqlite against a
//     specific schema; Swift has no equivalent in this package and inventing one
//     would be a database, not a port. What ports is the SHAPE it stores -
//     JobSpec and ApprovedDocument - plus `CareerStore`, the protocol a host
//     satisfies with whatever it already uses. That keeps the seam and drops the
//     driver.
//
//   • The interview script is DATA, and it is the part most worth carrying over
//     exactly: the wording was chosen for people whose work is piece work and a
//     stall, and paraphrasing it in a port would quietly change the product. Same
//     for IsDecline's word list, which accepts "cha", "hayi", "nee", "aowa" and
//     "tjhe" - a person declining in their own language is still declining.
//
//   • C# `Sum(q => q.Seconds)` over a TimeSpan → `length` in seconds as a
//     `TimeInterval`.

import Foundation

// MARK: - The profile

public struct ProfileIdentity: Sendable, Equatable, Codable {
    public let fullName: String
    public let headline: String
    public let phone: String?
    public let email: String?
    public let location: String?
    public let summary: String?

    public init(fullName: String = "", headline: String = "", phone: String? = nil,
                email: String? = nil, location: String? = nil, summary: String? = nil) {
        self.fullName = fullName
        self.headline = headline
        self.phone = phone
        self.email = email
        self.location = location
        self.summary = summary
    }
}

public struct ProfileSkill: Sendable, Equatable, Codable {
    public let name: String
    public let years: Double?
    public let evidenceHistoryId: Int64?
    public let id: Int64

    public init(name: String, years: Double? = nil,
                evidenceHistoryId: Int64? = nil, id: Int64 = 0) {
        self.name = name
        self.years = years
        self.evidenceHistoryId = evidenceHistoryId
        self.id = id
    }
}

public struct ProfileHistory: Sendable, Equatable, Codable {
    public let role: String
    public let organisation: String?
    /// Whether this was formal employment. False is not a gap - see `ProfileToCv`,
    /// which prints "Self-employed" rather than leaving the line blank.
    public let formal: Bool
    public let start: String?
    public let end: String?
    public let achievements: [String]?
    public let id: Int64

    public init(role: String, organisation: String? = nil, formal: Bool = true,
                start: String? = nil, end: String? = nil,
                achievements: [String]? = nil, id: Int64 = 0) {
        self.role = role
        self.organisation = organisation
        self.formal = formal
        self.start = start
        self.end = end
        self.achievements = achievements
        self.id = id
    }
}

public struct ProfileEducation: Sendable, Equatable, Codable {
    public let qualification: String
    public let institution: String?
    public let year: String?
    public let completed: Bool
    public let id: Int64

    public init(qualification: String, institution: String? = nil, year: String? = nil,
                completed: Bool = true, id: Int64 = 0) {
        self.qualification = qualification
        self.institution = institution
        self.year = year
        self.completed = completed
        self.id = id
    }
}

public struct ProfileCertification: Sendable, Equatable, Codable {
    public let name: String
    public let issuer: String?
    public let year: String?
    public let expires: String?
    public let id: Int64

    public init(name: String, issuer: String? = nil, year: String? = nil,
                expires: String? = nil, id: Int64 = 0) {
        self.name = name
        self.issuer = issuer
        self.year = year
        self.expires = expires
        self.id = id
    }
}

public struct ProfileLanguage: Sendable, Equatable, Codable {
    public let name: String
    public let level: String?
    public let id: Int64

    public init(name: String, level: String? = nil, id: Int64 = 0) {
        self.name = name
        self.level = level
        self.id = id
    }
}

/// Everything the app knows about somebody's working life.
public struct CareerProfile: Sendable, Equatable, Codable {
    public let identity: ProfileIdentity
    public let history: [ProfileHistory]
    public let skills: [ProfileSkill]
    public let education: [ProfileEducation]
    public let certifications: [ProfileCertification]
    public let languages: [ProfileLanguage]

    public init(identity: ProfileIdentity = ProfileIdentity(),
                history: [ProfileHistory] = [],
                skills: [ProfileSkill] = [],
                education: [ProfileEducation] = [],
                certifications: [ProfileCertification] = [],
                languages: [ProfileLanguage] = []) {
        self.identity = identity
        self.history = history
        self.skills = skills
        self.education = education
        self.certifications = certifications
        self.languages = languages
    }

    public static let empty = CareerProfile()

    /// How complete this profile is, 0...1.
    ///
    /// THE WEIGHTS ARE THE PRODUCT'S OPINION, not a guess: a name and a phone
    /// number weigh 3 each and work history 4, because without those an employer
    /// cannot call you and has nothing to read. Education weighs 1. Carrying the
    /// numbers across unchanged is the point - a port that "tidied" them would
    /// show a different percentage for the same person.
    public func completeness() -> Double {
        var score = 0.0
        var total = 0.0
        func weigh(_ have: Bool, _ weight: Double) {
            total += weight
            if have { score += weight }
        }
        func filled(_ s: String?) -> Bool {
            guard let s else { return false }
            return !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }

        weigh(filled(identity.fullName), 3)
        weigh(filled(identity.phone), 3)
        weigh(filled(identity.headline), 2)
        weigh(filled(identity.location), 1)
        weigh(!history.isEmpty, 4)
        weigh(!skills.isEmpty, 2)
        weigh(!education.isEmpty, 1)
        weigh(!certifications.isEmpty, 1)
        weigh(!languages.isEmpty, 1)
        return total <= 0 ? 0 : score / total
    }
}

// MARK: - The interview

/// One thing the interview can learn about somebody.
public enum ProfileField: String, Sendable, Equatable, Codable, CaseIterable {
    case fullName, phone, headline, location
    case workRole, workOrganisation, workWhen, workDid
    case skills, education, certification, languages, summary
}

/// One question, why it is being asked, and how long an answer usually takes.
public struct InterviewQuestion: Sendable, Equatable {
    public let field: ProfileField
    /// What the person is asked.
    public let ask: String
    /// Why it matters - shown to them, not to us.
    public let why: String
    /// Whether the answer should be read back for confirmation.
    public let verify: Bool
    /// Rough seconds to answer, used to estimate the interview length.
    public let seconds: Int

    public init(field: ProfileField, ask: String, why: String,
                verify: Bool = false, seconds: Int = 30) {
        self.field = field
        self.ask = ask
        self.why = why
        self.verify = verify
        self.seconds = seconds
    }
}

/// The scripted interview that fills a `CareerProfile` by asking.
public enum CareerInterview {

    /// The script, in order.
    ///
    /// WORDED FOR THE PERSON IT IS FOR. "What is the last work you did? It does
    /// not have to be a formal job" is not the same question as "What was your
    /// last job", and the difference is the whole reason this product exists for
    /// somebody whose work was piece work, a stall, or a family business. The
    /// strings are carried over verbatim.
    public static let script: [InterviewQuestion] = [
        InterviewQuestion(field: .fullName,
            ask: "What is your full name?",
            why: "It goes at the top of your CV, spelled the way you want it.",
            verify: true, seconds: 20),
        InterviewQuestion(field: .phone,
            ask: "What number should an employer call?",
            why: "Without this nobody can offer you the job.",
            verify: true, seconds: 25),
        InterviewQuestion(field: .headline,
            ask: "What kind of work are you looking for?",
            why: "It tells the employer in three words what you are, before they read anything else.",
            seconds: 25),
        InterviewQuestion(field: .location,
            ask: "Where do you live? Just the area and the city.",
            why: "Employers filter by who can get to work.",
            verify: true, seconds: 20),
        InterviewQuestion(field: .workRole,
            ask: "What is the last work you did? It does not have to be a formal job.",
            why: "Piece work, a stall, helping in a family business — all of it counts.",
            seconds: 40),
        InterviewQuestion(field: .workOrganisation,
            ask: "Who was that for? Say skip if you worked for yourself.",
            why: "A name an employer recognises helps, but working for yourself is not a gap.",
            seconds: 25),
        InterviewQuestion(field: .workWhen,
            ask: "Roughly when was that, and are you still doing it?",
            why: "Approximate is fine — 'about two years, until last winter'.",
            seconds: 30),
        InterviewQuestion(field: .workDid,
            ask: "What did you actually do there? Tell me two or three things.",
            why: "This is the part that gets read. What you did beats what you were called.",
            seconds: 70),
        InterviewQuestion(field: .skills,
            ask: "What are you good at? Machines, tools, systems, dealing with people.",
            why: "These are what a job advert matches against.",
            seconds: 60),
        InterviewQuestion(field: .certification,
            ask: "Do you have a licence or certificate? A driver's code, PSIRA, first aid?",
            why: "For a lot of jobs this is the thing that decides it.",
            seconds: 40),
        InterviewQuestion(field: .education,
            ask: "What school or training did you finish, and when?",
            why: "If you did not finish, say so — it is still worth putting down.",
            seconds: 40),
        InterviewQuestion(field: .languages,
            ask: "Which languages do you speak?",
            why: "In this country that is a qualification, not a detail.",
            seconds: 30),
        InterviewQuestion(field: .summary,
            ask: "Anything else an employer should know about you?",
            why: "One or two sentences in your own words.",
            seconds: 45),
    ]

    /// Roughly how long the whole interview takes.
    public static var length: TimeInterval {
        TimeInterval(script.reduce(0) { $0 + $1.seconds })
    }

    /// The next unanswered question, or nil when the profile is complete.
    public static func next(_ profile: CareerProfile) -> InterviewQuestion? {
        script.first { !answered(profile, $0.field) }
    }

    /// Whether this profile already holds an answer for `field`.
    public static func answered(_ p: CareerProfile, _ field: ProfileField) -> Bool {
        func filled(_ s: String?) -> Bool {
            guard let s else { return false }
            return !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
        switch field {
        case .fullName:         return filled(p.identity.fullName)
        case .phone:            return filled(p.identity.phone)
        case .headline:         return filled(p.identity.headline)
        case .location:         return filled(p.identity.location)
        case .workRole:         return !p.history.isEmpty
        case .workOrganisation: return !p.history.isEmpty && p.history[0].organisation != nil
        case .workWhen:         return !p.history.isEmpty && p.history[0].start != nil
        case .workDid:          return !p.history.isEmpty && !(p.history[0].achievements ?? []).isEmpty
        case .skills:           return !p.skills.isEmpty
        case .education:        return !p.education.isEmpty
        case .certification:    return !p.certifications.isEmpty
        case .languages:        return !p.languages.isEmpty
        case .summary:          return false
        }
    }

    /// Whether an answer means "skip this one".
    ///
    /// IN THEIR LANGUAGE, NOT JUST IN ENGLISH. "cha" and "hayi" (isiZulu /
    /// isiXhosa), "nee" (Afrikaans), "aowa" and "tjhe" (Sesotho / Setswana) are
    /// all a person declining, and an interview that only understood "no" would
    /// record the word itself as their answer.
    public static func isDecline(_ answer: String?) -> Bool {
        guard let answer else { return true }
        let a = answer.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if a.isEmpty { return true }
        return ["skip", "none", "no", "nothing", "next", "pass",
                "cha", "hayi", "nee", "aowa", "tjhe"].contains(a)
    }
}

// MARK: - Tailoring a profile to one job

/// A job advert somebody wants to aim their CV at.
public struct JobSpec: Sendable, Equatable, Codable {
    public let title: String
    public let employer: String?
    public let text: String
    public let source: String
    public let added: Date?
    public let id: Int64

    public init(title: String, employer: String? = nil, text: String,
                source: String = "typed", added: Date? = nil, id: Int64 = 0) {
        self.title = title
        self.employer = employer
        self.text = text
        self.source = source
        self.added = added
        self.id = id
    }
}

/// A rendered CV the person approved, kept with the facts it was built from.
public struct ApprovedDocument: Sendable, Equatable {
    public let specId: Int64?
    public let pdf: Data
    /// The profile ids that went into it - so a claim on a CV can always be
    /// traced back to the thing the person actually said.
    public let selectedFacts: [Int64]
    public let approved: Date
    public let id: Int64

    public init(specId: Int64?, pdf: Data, selectedFacts: [Int64],
                approved: Date, id: Int64 = 0) {
        self.specId = specId
        self.pdf = pdf
        self.selectedFacts = selectedFacts
        self.approved = approved
        self.id = id
    }
}

/// Which of a person's real experience to put on a CV for one job.
public struct TailoringChoice: Sendable, Equatable {
    public let historyIds: [Int64]
    public let skillIds: [Int64]
    public let headline: String?
    /// One sentence, written for the applicant rather than for us.
    public let reasoning: String

    public init(historyIds: [Int64], skillIds: [Int64],
                headline: String? = nil, reasoning: String) {
        self.historyIds = historyIds
        self.skillIds = skillIds
        self.headline = headline
        self.reasoning = reasoning
    }
}

/// Builds the prompt that asks a model to choose, and reads its answer back.
public enum ProfileTailoring {

    /// The prompt.
    ///
    /// "You may only choose from the numbered items. You may not add anything."
    /// is the load-bearing sentence: the model is picking from what the person
    /// actually said, never inventing experience onto somebody's CV. The parser
    /// below enforces the same rule a second time, because a prompt is a request
    /// and not a guarantee.
    public static func buildPrompt(profile: CareerProfile, spec: JobSpec) -> String {
        var s = ""
        s += "You are choosing which of a person's REAL experience to put on a CV for one job.\n"
        s += "You may only choose from the numbered items. You may not add anything.\n"
        s += "\n"
        s += "THE JOB:\n"
        s += spec.title + (spec.employer == nil ? "" : " at \(spec.employer!)") + "\n"
        s += trim(spec.text, max: 1200) + "\n"
        s += "\n"
        s += "THEIR WORK (id: what they did):\n"
        for h in profile.history {
            let org = (h.organisation?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
                ? "self-employed" : h.organisation!
            let did = (h.achievements?.isEmpty == false)
                ? " — " + h.achievements!.joined(separator: "; ") : ""
            s += "\(h.id): \(h.role) at \(org)\(did)\n"
        }
        s += "\n"
        s += "THEIR SKILLS (id: skill):\n"
        for k in profile.skills { s += "\(k.id): \(k.name)\n" }
        s += "\n"
        s += "Answer with ONLY these three lines and nothing else:\n"
        s += "WORK: comma-separated ids, most relevant first\n"
        s += "SKILLS: comma-separated ids, most relevant first\n"
        s += "WHY: one short sentence for the applicant\n"
        return s
    }

    /// Reads the model's answer, keeping only ids the person actually has.
    ///
    /// AN UNPARSEABLE ANSWER KEEPS EVERYTHING rather than producing an empty CV.
    /// A model that ignores the format, or names an id that is not theirs, must
    /// not be able to delete somebody's work history - so an empty selection
    /// falls back to the whole profile and says so in words they can read.
    public static func parse(_ answer: String?, profile: CareerProfile) -> TailoringChoice {
        let validHistory = Set(profile.history.map(\.id))
        let validSkills = Set(profile.skills.map(\.id))
        var work: [Int64] = []
        var skills: [Int64] = []
        var why = ""

        for raw in (answer ?? "").split(separator: "\n", omittingEmptySubsequences: false) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.lowercased().hasPrefix("work:") {
                work += ids(String(line.dropFirst(5)), valid: validHistory)
            } else if line.lowercased().hasPrefix("skills:") {
                skills += ids(String(line.dropFirst(7)), valid: validSkills)
            } else if line.lowercased().hasPrefix("why:") {
                why = String(line.dropFirst(4)).trimmingCharacters(in: .whitespaces)
            }
        }

        if work.isEmpty { work = profile.history.map(\.id) }
        if skills.isEmpty { skills = profile.skills.map(\.id) }

        return TailoringChoice(
            historyIds: distinct(work),
            skillIds: distinct(skills),
            headline: nil,
            reasoning: why.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? "Kept everything — this is your full history."
                : why)
    }

    /// Every profile id this choice draws on.
    public static func selectedFacts(_ choice: TailoringChoice) -> [Int64] {
        choice.historyIds + choice.skillIds
    }

    /// Order-preserving distinct - the model was asked for "most relevant first",
    /// so a Set would throw away the one thing the ordering was carrying.
    private static func distinct(_ xs: [Int64]) -> [Int64] {
        var seen = Set<Int64>()
        return xs.filter { seen.insert($0).inserted }
    }

    private static func ids(_ csv: String, valid: Set<Int64>) -> [Int64] {
        csv.split(separator: ",")
            .compactMap { Int64($0.trimmingCharacters(in: .whitespaces)) }
            .filter(valid.contains)
    }

    private static func trim(_ s: String, max: Int) -> String {
        s.count <= max ? s : String(s.prefix(max)) + "…"
    }
}

// MARK: - Storage seam

/// Where a career profile, the job specs and the approved CVs are kept.
///
/// The C# ships one implementation, SqliteCareerStore, which does not port - it
/// is Microsoft.Data.Sqlite against a fixed schema. This is the half that is
/// portable: what a store must be able to do, so a host can satisfy it with
/// whatever it already has.
public protocol CareerStore: Sendable {
    func loadProfile() async throws -> CareerProfile
    func save(_ profile: CareerProfile) async throws

    func specs() async throws -> [JobSpec]
    @discardableResult func add(_ spec: JobSpec) async throws -> Int64

    func approvedDocuments() async throws -> [ApprovedDocument]
    @discardableResult func approve(_ document: ApprovedDocument) async throws -> Int64
}

// MARK: - Profile → CV

/// Turns a `CareerProfile` into the `CvDocument` the document engine renders.
public enum ProfileToCv {

    /// Renders the profile, optionally narrowed to the ids a tailoring choice
    /// selected. Passing nil keeps everything.
    public static func render(_ profile: CareerProfile, only: Set<Int64>? = nil) -> CvDocument {
        let id = profile.identity

        // A CV with no name still has to render - it is shown back to the person
        // mid-interview, before they have answered anything.
        let name = blank(id.fullName) ?? "Your name"
        let headline = blank(id.headline) ?? mostRecentRole(profile) ?? ""

        let history = profile.history
            .filter { only == nil || only!.contains($0.id) }
            .map(toExperience)

        let skills = profile.skills
            .filter { only == nil || only!.contains($0.id) }
            .map(formatSkill)

        return CvDocument(
            fullName: name,
            headline: headline,
            contact: CvContact(email: blank(id.email),
                               phone: blank(id.phone),
                               location: blank(id.location)),
            summary: blank(id.summary),
            experience: history,
            education: profile.education.map(toEducation),
            skills: skills,
            // Absent, not empty: the template hides the whole section rather than
            // printing a heading with nothing under it.
            certifications: profile.certifications.isEmpty ? nil
                : profile.certifications.map { CvCertification(name: $0.name, issuer: $0.issuer, year: $0.year) },
            languages: profile.languages.isEmpty ? nil
                : profile.languages.map { $0.level == nil ? $0.name : "\($0.name) (\($0.level!))" })
    }

    /// SELF-EMPLOYED IS NOT A BLANK. Somebody who worked for themselves has an
    /// organisation of "Self-employed", not an empty line that reads on a CV as
    /// though they could not remember who they worked for.
    private static func toExperience(_ h: ProfileHistory) -> CvExperience {
        CvExperience(
            title: h.role,
            organisation: blank(h.organisation) ?? (h.formal ? "" : "Self-employed"),
            location: nil,
            startDate: h.start ?? "",
            endDate: h.end,
            highlights: h.achievements ?? [])
    }

    /// UNFINISHED STUDY STILL GOES ON, marked. The interview tells people "if you
    /// did not finish, say so — it is still worth putting down", so dropping it
    /// here would break a promise the question made.
    private static func toEducation(_ e: ProfileEducation) -> CvEducation {
        CvEducation(
            qualification: e.completed ? e.qualification : e.qualification + " (not completed)",
            institution: e.institution ?? "",
            location: nil,
            startDate: nil,
            endDate: e.year)
    }

    private static func formatSkill(_ s: ProfileSkill) -> String {
        guard let y = s.years, y > 0 else { return s.name }
        // "0.#" in C#: drop a trailing .0, keep one decimal otherwise.
        let n = y.rounded(.towardZero) == y
            ? String(Int(y))
            : String(format: "%.1f", y)
        return "\(s.name) (\(n) yr\(y >= 2 ? "s" : ""))"
    }

    private static func mostRecentRole(_ p: CareerProfile) -> String? {
        p.history.first?.role
    }

    private static func blank(_ s: String?) -> String? {
        guard let s else { return nil }
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? nil : t
    }
}
