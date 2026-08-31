// CareerTests.swift
//
// The parser and the CV mapper carry the risk here: one puts a model's answer
// onto somebody's CV, the other decides how their work is described to an
// employer. Both are tested against what the C# promises, not against the Swift.

import XCTest
@testable import CircleAI

final class CareerTests: XCTestCase {

    private func profile() -> CareerProfile {
        CareerProfile(
            identity: ProfileIdentity(fullName: "Thabo Mokoena", headline: "Driver",
                                      phone: "+27 82 555 0142", location: "Soweto"),
            history: [
                ProfileHistory(role: "Delivery driver", organisation: "Aurora", start: "2023",
                               achievements: ["Ran a fixed route", "Kept the vehicle log"], id: 1),
                ProfileHistory(role: "Stall holder", organisation: nil, formal: false,
                               start: "2021", id: 2),
            ],
            skills: [ProfileSkill(name: "Code 10 licence", id: 10),
                     ProfileSkill(name: "Stock control", years: 3, id: 11)],
            education: [ProfileEducation(qualification: "Matric", institution: "Morris Isaacson",
                                         year: "2019", id: 20)],
            certifications: [ProfileCertification(name: "First aid", id: 30)],
            languages: [ProfileLanguage(name: "isiZulu", level: "Home", id: 40)])
    }

    // MARK: - Completeness

    func test_empty_profile_is_zero_complete() {
        XCTAssertEqual(CareerProfile.empty.completeness(), 0, accuracy: 1e-9)
    }

    func test_completeness_weights_a_phone_number_as_heavily_as_a_name() {
        // Both weigh 3 in the C#, because without either an employer cannot hire
        // you. If a port quietly reweighted these the percentage would drift.
        let name = CareerProfile(identity: ProfileIdentity(fullName: "A"))
        let phone = CareerProfile(identity: ProfileIdentity(phone: "1"))
        XCTAssertEqual(name.completeness(), phone.completeness(), accuracy: 1e-9)
    }

    func test_work_history_is_the_heaviest_single_thing() {
        let work = CareerProfile(history: [ProfileHistory(role: "r")])
        let education = CareerProfile(education: [ProfileEducation(qualification: "q")])
        XCTAssertGreaterThan(work.completeness(), education.completeness())
    }

    func test_a_full_profile_is_complete() {
        XCTAssertEqual(profile().completeness(), 1.0, accuracy: 1e-9)
    }

    func test_whitespace_does_not_count_as_an_answer() {
        XCTAssertEqual(CareerProfile(identity: ProfileIdentity(fullName: "   ")).completeness(),
                       0, accuracy: 1e-9)
    }

    // MARK: - Interview

    func test_the_interview_asks_for_a_name_first() {
        XCTAssertEqual(CareerInterview.next(CareerProfile.empty)?.field, .fullName)
    }

    func test_the_interview_skips_what_is_already_known() {
        let p = CareerProfile(identity: ProfileIdentity(fullName: "Thabo"))
        XCTAssertEqual(CareerInterview.next(p)?.field, .phone)
    }

    func test_every_question_says_what_it_is_and_why() {
        for q in CareerInterview.script {
            XCTAssertFalse(q.ask.isEmpty, "\(q.field) has no question")
            XCTAssertFalse(q.why.isEmpty, "\(q.field) does not say why it is asked")
            XCTAssertGreaterThan(q.seconds, 0)
        }
    }

    func test_the_interview_is_a_few_minutes_not_an_hour() {
        XCTAssertGreaterThan(CareerInterview.length, 60)
        XCTAssertLessThan(CareerInterview.length, 15 * 60)
    }

    func test_declining_works_in_the_languages_people_actually_speak() {
        for word in ["skip", "none", "no", "cha", "hayi", "nee", "aowa", "tjhe"] {
            XCTAssertTrue(CareerInterview.isDecline(word), "'\(word)' should decline")
            XCTAssertTrue(CareerInterview.isDecline("  " + word.uppercased() + "  "),
                          "'\(word)' should decline regardless of case or spacing")
        }
        XCTAssertTrue(CareerInterview.isDecline(nil))
        XCTAssertTrue(CareerInterview.isDecline("   "))
        XCTAssertFalse(CareerInterview.isDecline("Delivery driver"))
    }
}

extension CareerTests {

    private func full() -> CareerProfile {
        CareerProfile(
            identity: ProfileIdentity(fullName: "Thabo Mokoena", headline: "Driver",
                                      phone: "+27 82 555 0142", location: "Soweto"),
            history: [
                ProfileHistory(role: "Delivery driver", organisation: "Aurora", start: "2023",
                               achievements: ["Ran a fixed route"], id: 1),
                ProfileHistory(role: "Stall holder", organisation: nil, formal: false,
                               start: "2021", id: 2),
            ],
            skills: [ProfileSkill(name: "Code 10 licence", id: 10),
                     ProfileSkill(name: "Stock control", years: 3, id: 11)],
            education: [ProfileEducation(qualification: "Matric", year: "2019", id: 20)],
            certifications: [ProfileCertification(name: "First aid", id: 30)],
            languages: [ProfileLanguage(name: "isiZulu", level: "Home", id: 40)])
    }

    // MARK: - Tailoring

    func test_parse_keeps_only_ids_the_person_actually_has() {
        // A model naming an id that is not theirs must not put it on the CV.
        let c = ProfileTailoring.parse("WORK: 1, 999\nSKILLS: 10\nWHY: Route work matches.",
                                       profile: full())
        XCTAssertEqual(c.historyIds, [1])
        XCTAssertEqual(c.skillIds, [10])
        XCTAssertEqual(c.reasoning, "Route work matches.")
    }

    func test_parse_preserves_the_order_the_model_chose() {
        // The prompt asks for "most relevant first"; a Set would discard that.
        XCTAssertEqual(ProfileTailoring.parse("WORK: 2, 1", profile: full()).historyIds, [2, 1])
    }

    func test_parse_drops_duplicates_without_reordering() {
        XCTAssertEqual(ProfileTailoring.parse("WORK: 2, 1, 2", profile: full()).historyIds, [2, 1])
    }

    func test_an_unparseable_answer_keeps_everything_rather_than_emptying_the_cv() {
        // A model that ignores the format must not delete somebody's work history.
        for answer in ["", "I cannot help with that.", "WORK: 999"] {
            let c = ProfileTailoring.parse(answer, profile: full())
            XCTAssertEqual(c.historyIds, [1, 2], "answer '\(answer)' emptied the history")
            XCTAssertEqual(c.skillIds, [10, 11])
            XCTAssertEqual(c.reasoning, "Kept everything — this is your full history.")
        }
    }

    func test_parse_tolerates_a_nil_answer() {
        XCTAssertEqual(ProfileTailoring.parse(nil, profile: full()).historyIds, [1, 2])
    }

    func test_prompt_forbids_inventing_experience() {
        let p = ProfileTailoring.buildPrompt(
            profile: full(), spec: JobSpec(title: "Driver", employer: "Aurora", text: "Deliveries"))
        XCTAssertTrue(p.contains("You may not add anything."),
                      "the prompt must forbid invention - it is the safety rule")
        XCTAssertTrue(p.contains("1: Delivery driver at Aurora"))
        XCTAssertTrue(p.contains("2: Stall holder at self-employed"))
    }

    func test_prompt_truncates_a_very_long_advert() {
        let p = ProfileTailoring.buildPrompt(
            profile: full(), spec: JobSpec(title: "T", text: String(repeating: "x", count: 5000)))
        XCTAssertTrue(p.contains("…"), "a 5000-character advert must be trimmed")
        XCTAssertLessThan(p.count, 4000)
    }

    func test_selected_facts_covers_both_lists() {
        let c = ProfileTailoring.parse("WORK: 1\nSKILLS: 11", profile: full())
        XCTAssertEqual(Set(ProfileTailoring.selectedFacts(c)), Set([1, 11]))
    }

    // MARK: - Profile to CV

    func test_self_employment_is_labelled_not_left_blank() {
        let cv = ProfileToCv.render(full())
        XCTAssertEqual(cv.experience.first { $0.title == "Stall holder" }?.organisation,
                       "Self-employed",
                       "a blank employer reads as though they could not remember")
    }

    func test_unfinished_study_is_kept_and_marked() {
        let p = CareerProfile(education: [ProfileEducation(qualification: "N4 Electrical",
                                                           completed: false)])
        XCTAssertEqual(ProfileToCv.render(p).education.first?.qualification,
                       "N4 Electrical (not completed)")
    }

    func test_skill_years_are_formatted_like_the_reference() {
        let p = CareerProfile(skills: [
            ProfileSkill(name: "Welding", years: 1),
            ProfileSkill(name: "Stock control", years: 3),
            ProfileSkill(name: "Forklift", years: 2.5),
            ProfileSkill(name: "Tiling"),
        ])
        XCTAssertEqual(ProfileToCv.render(p).skills,
                       ["Welding (1 yr)", "Stock control (3 yrs)",
                        "Forklift (2.5 yrs)", "Tiling"])
    }

    func test_a_nameless_profile_still_renders() {
        // The CV is shown back mid-interview, before anything is answered.
        XCTAssertEqual(ProfileToCv.render(CareerProfile.empty).fullName, "Your name")
    }

    func test_headline_falls_back_to_the_most_recent_role() {
        let p = CareerProfile(history: [ProfileHistory(role: "Delivery driver")])
        XCTAssertEqual(ProfileToCv.render(p).headline, "Delivery driver")
    }

    func test_empty_sections_are_absent_rather_than_empty() {
        // nil hides the section; [] would print a heading with nothing under it.
        let cv = ProfileToCv.render(CareerProfile.empty)
        XCTAssertNil(cv.certifications)
        XCTAssertNil(cv.languages)
    }

    func test_render_honours_a_tailoring_selection() {
        let choice = ProfileTailoring.parse("WORK: 1\nSKILLS: 10", profile: full())
        let cv = ProfileToCv.render(full(), only: Set(ProfileTailoring.selectedFacts(choice)))
        XCTAssertEqual(cv.experience.count, 1)
        XCTAssertEqual(cv.experience[0].title, "Delivery driver")
        XCTAssertEqual(cv.skills, ["Code 10 licence"])
    }

    func test_a_language_with_a_level_prints_it() {
        XCTAssertEqual(ProfileToCv.render(full()).languages, ["isiZulu (Home)"])
    }
}
