import XCTest
@testable import CircleAI

/// Records what an adapter actually sent, so the decoration can be checked
/// without a model anywhere near it.
final class RecordingSession: ICompanionSession, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var sent: [String] = []
    private(set) var streamed: [String] = []
    private(set) var agented: [String] = []

    var sessionId: String { "s1" }
    var identityId: String { "i1" }
    var interface: InterfaceKind { .web }
    var history: [CompanionTurn] { [] }
    var proactiveEvents: AsyncStream<CompanionProactiveEvent> {
        AsyncStream { $0.finish() }
    }

    func getContext() -> CompanionContext {
        CompanionContext(identityId: "i1", displayName: "Nandi", interface: .web,
                         personaHints: "", affectSummary: "",
                         recentMemorySnippets: [], activeGoals: [])
    }
    func refreshContext() async throws {}
    func signalFeedback(positive: Bool, note: String?) async throws {}

    func send(_ message: String) async throws -> String {
        lock.lock(); sent.append(message); lock.unlock(); return "ok"
    }
    func stream(_ message: String) -> AsyncStream<String> {
        lock.lock(); streamed.append(message); lock.unlock()
        return AsyncStream { $0.finish() }
    }
    func agent(_ instruction: String) async throws -> String {
        lock.lock(); agented.append(instruction); lock.unlock(); return "ok"
    }
}

final class CompanionAdapterTests: XCTestCase {

    // MARK: - The decoration contract

    func testAnAdapterPutsItsDomainSnippetInFrontOfEveryConversationalCall() async throws {
        let inner = RecordingSession()
        let adapter = RetailCompanionAdapter(inner)

        _ = try await adapter.send("what should I reorder")
        _ = adapter.stream("what should I reorder")
        _ = try await adapter.agent("what should I reorder")

        for captured in [inner.sent, inner.streamed, inner.agented] {
            XCTAssertEqual(1, captured.count)
            XCTAssertTrue(captured[0].hasPrefix(RetailDomainContext.systemPromptSnippet),
                          "the domain snippet did not lead")
            XCTAssertTrue(captured[0].hasSuffix("\n\nwhat should I reorder"),
                          "the message was not preserved verbatim after a blank line")
        }
    }

    func testIdentityAndContextAreForwardedUntouched() {
        // An adapter is a DECORATOR, not a session. Anything it invented here
        // would be a second identity for one conversation.
        let inner = RecordingSession()
        let adapter = RetailCompanionAdapter(inner)
        XCTAssertEqual(inner.sessionId, adapter.sessionId)
        XCTAssertEqual(inner.identityId, adapter.identityId)
        XCTAssertEqual(inner.interface, adapter.interface)
        XCTAssertEqual(inner.history.count, adapter.history.count)
    }

    func testAHelperGoesStraightToTheAgentWithoutTheSnippet() async throws {
        // The helpers already carry their own fully-specified instruction; the
        // snippet is for the caller's own words, not for a prompt this file
        // wrote. Prefixing twice would say the same thing to the model twice.
        let inner = RecordingSession()
        _ = try await RetailCompanionAdapter(inner)
            .analyseStockHealth(sku: "SKU-1", onHand: 12, weeklySales: 30)
        XCTAssertEqual(1, inner.agented.count)
        XCTAssertFalse(inner.agented[0].hasPrefix(RetailDomainContext.systemPromptSnippet))
    }

    // MARK: - The prompts, which are generated and therefore worth checking

    func testAHelperInterpolatesEveryArgumentItWasGiven() async throws {
        let inner = RecordingSession()
        _ = try await RetailCompanionAdapter(inner)
            .analyseStockHealth(sku: "SKU-42", onHand: 7, weeklySales: 31)
        let p = inner.agented[0]
        XCTAssertTrue(p.contains("SKU-42"))
        XCTAssertTrue(p.contains("7 units on hand"))
        XCTAssertTrue(p.contains("31 weekly sales"))
        // And the surrounding instruction survived the transcription whole.
        XCTAssertTrue(p.contains("Recommend reorder point, safety stock, and EOQ."))
    }

    func testNoGeneratedPromptIsEmptyOrLeftAnUnsubstitutedBrace() async throws {
        // A generator that lost a literal produces an empty prompt, and one
        // that missed an interpolation leaves a literal {name} in the text.
        // Both look fine until a model is reading it.
        let inner = RecordingSession()

        _ = try await RetailCompanionAdapter(inner).planPromotion(objective: "A", constraints: "B")
        _ = try await LegalCompanionAdapter(inner).reviewContractClauses(contractText: "A", focusArea: "B")
        _ = try await PetsCompanionAdapter(inner).triageSymptom(species: "dog", breed: "collie", symptom: "limp")
        _ = try await HealthcareCompanionAdapter(inner).documentClinicalNote(patientVisitSummary: "A")

        XCTAssertEqual(4, inner.agented.count)
        for prompt in inner.agented {
            XCTAssertFalse(prompt.isEmpty, "a generated prompt came out empty")
            XCTAssertFalse(prompt.contains("{"), "an interpolation was not substituted: \(prompt)")
            XCTAssertGreaterThan(prompt.count, 30, "a generated prompt was suspiciously short")
        }
    }

    func testACurrencyArgumentIsInterpolatedAsANumber() async throws {
        // The C# writes {budget:C}, which renders against the machine's CURRENT
        // CULTURE — R1 200,00 on one device and $1,200.00 on another. A prompt
        // that differs by locale is not the same prompt, so the specifier is
        // dropped and the number goes in plainly.
        let inner = RecordingSession()
        _ = try await RetailCompanionAdapter(inner)
            .designPromotion(goal: "clearance", category: "footwear", budget: Decimal(1200))
        let p = inner.agented[0]
        XCTAssertTrue(p.contains("1200"), "the budget did not reach the prompt: \(p)")
        XCTAssertFalse(p.contains(":C"))
    }

    // MARK: - Every adapter, mechanically

    func testEveryNewAdapterDecoratesAndForwardsCorrectly() async throws {
        // Twenty-six new types generated from one template: if the template is
        // right for one it is right for all, and this is what says so rather
        // than twenty-six near-identical test methods.
        let cases: [(String, (ICompanionSession) -> ICompanionSession, String)] = [
            ("Business", { BusinessCompanionAdapter($0) }, BusinessDomainContext.systemPromptSnippet),
            ("Commerce", { CommerceCompanionAdapter($0) }, CommerceDomainContext.systemPromptSnippet),
            ("CommerceAccounting", { CommerceAccountingCompanionAdapter($0) },
             CommerceAccountingDomainContext.systemPromptSnippet),
            ("CommerceFinance", { CommerceFinanceCompanionAdapter($0) },
             CommerceFinanceDomainContext.systemPromptSnippet),
            ("CommerceIntegrationPayFast", { CommerceIntegrationPayFastCompanionAdapter($0) },
             CommerceIntegrationPayFastDomainContext.systemPromptSnippet),
            ("CommerceIntegrationXero", { CommerceIntegrationXeroCompanionAdapter($0) },
             CommerceIntegrationXeroDomainContext.systemPromptSnippet),
            ("Education", { EducationCompanionAdapter($0) }, EducationDomainContext.systemPromptSnippet),
            ("Elderly", { ElderlyCompanionAdapter($0) }, ElderlyDomainContext.systemPromptSnippet),
            ("Family", { FamilyCompanionAdapter($0) }, FamilyDomainContext.systemPromptSnippet),
            ("HR", { HRCompanionAdapter($0) }, HRDomainContext.systemPromptSnippet),
            ("Healthcare", { HealthcareCompanionAdapter($0) }, HealthcareDomainContext.systemPromptSnippet),
            ("Home", { HomeCompanionAdapter($0) }, HomeDomainContext.systemPromptSnippet),
            ("Legal", { LegalCompanionAdapter($0) }, LegalDomainContext.systemPromptSnippet),
            ("Logistics", { LogisticsCompanionAdapter($0) }, LogisticsDomainContext.systemPromptSnippet),
            ("Parenting", { ParentingCompanionAdapter($0) }, ParentingDomainContext.systemPromptSnippet),
            ("Personal", { PersonalCompanionAdapter($0) }, PersonalDomainContext.systemPromptSnippet),
            ("PersonalFinance", { PersonalFinanceCompanionAdapter($0) },
             PersonalFinanceDomainContext.systemPromptSnippet),
            ("PersonalHealth", { PersonalHealthCompanionAdapter($0) },
             PersonalHealthDomainContext.systemPromptSnippet),
            ("PersonalMental", { PersonalMentalCompanionAdapter($0) },
             PersonalMentalDomainContext.systemPromptSnippet),
            ("Pets", { PetsCompanionAdapter($0) }, PetsDomainContext.systemPromptSnippet),
            ("RealEstate", { RealEstateCompanionAdapter($0) }, RealEstateDomainContext.systemPromptSnippet),
            ("Retail", { RetailCompanionAdapter($0) }, RetailDomainContext.systemPromptSnippet),
            ("Safety", { SafetyCompanionAdapter($0) }, SafetyDomainContext.systemPromptSnippet),
            ("SafetyChild", { SafetyChildCompanionAdapter($0) },
             SafetyChildDomainContext.systemPromptSnippet),
        ]

        XCTAssertEqual(24, cases.count)

        for (name, make, snippet) in cases {
            let inner = RecordingSession()
            _ = try await make(inner).send("hello")
            XCTAssertEqual(1, inner.sent.count, "\(name) did not forward")
            XCTAssertEqual("\(snippet)\n\nhello", inner.sent[0],
                           "\(name) did not decorate with its own snippet")
        }
    }

    func testEveryDomainSnippetNamesItsDomain() {
        // Every snippet in the reference opens with [DOMAIN: X]. It is what
        // tells a model which hat it is wearing, and one without it is one that
        // got truncated somewhere in transcription.
        let snippets = [
            BusinessDomainContext.systemPromptSnippet,
            HealthcareDomainContext.systemPromptSnippet,
            LegalDomainContext.systemPromptSnippet,
            SafetyDomainContext.systemPromptSnippet,
            SafetyChildDomainContext.systemPromptSnippet,
            PersonalMentalDomainContext.systemPromptSnippet,
        ]
        for s in snippets {
            XCTAssertTrue(s.hasPrefix("[DOMAIN: "), "snippet did not name its domain: \(s.prefix(40))")
            XCTAssertTrue(s.contains("]"))
        }
    }

    func testTheSafetySnippetsCarryTheirEmergencyNumbers() {
        // The one domain where the model being told the wrong thing about what
        // to do next costs something other than tokens. Losing these in a port
        // is silent and would not be noticed until it mattered.
        XCTAssertTrue(SafetyDomainContext.systemPromptSnippet.contains("10111"))
        XCTAssertTrue(SafetyDomainContext.systemPromptSnippet.contains("10177"))
        XCTAssertTrue(SafetyChildDomainContext.systemPromptSnippet.contains("10111"))
        XCTAssertTrue(SafetyChildDomainContext.systemPromptSnippet.contains("116"))
    }

    func testTheChildDomainCarriesTheStricterPrivacyFlag() {
        // POPIA_Children rather than plain POPIA — a stricter regime for a
        // stricter subject. Anything matching on the flag string has to know.
        XCTAssertTrue(SafetyChildDomainContext.complianceFlags.contains("POPIA_Children"))
        XCTAssertFalse(SafetyChildDomainContext.complianceFlags.contains("POPIA"))
        XCTAssertTrue(SafetyDomainContext.complianceFlags.contains("POPIA"))
    }

    func testEveryDomainContextOffersFlagsAndTools() {
        // A domain with no compliance flags is one nobody wrote the rules down
        // for; one with no tools gives the model nothing to reach for.
        XCTAssertFalse(SafetyDomainContext.complianceFlags.isEmpty)
        XCTAssertFalse(SafetyDomainContext.suggestedTools.isEmpty)
        XCTAssertFalse(SafetyChildDomainContext.complianceFlags.isEmpty)
        XCTAssertFalse(SafetyChildDomainContext.suggestedTools.isEmpty)
    }
}
