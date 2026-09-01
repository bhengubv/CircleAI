import XCTest
@testable import CircleAI

/// Catalogues and money.
final class DistributionRailsTests: XCTestCase {

    // A default that CLAIMED approval would be a lie in code.
    func testRegulatoryApprovalsDefaultToNotApproved() {
        XCTAssertFalse(DefaultSarbSandboxStatus().approved)
        XCTAssertFalse(DefaultIcasaApprovalStatus().approved)
    }

    func testThePostureIsStatedInOneLine() {
        XCTAssertEqual(DefaultLawfulInterceptCompliance().posture,
                       "Money decryptable to law, comms permanently blind")
    }

    // Defaulting to no review for an unrecognised language is exactly the
    // failure this exists to prevent.
    func testElderReviewIsRequiredForEveryLanguage() {
        let p = DefaultIndigenousKnowledgeProtocols()
        for lang in ["zul", "xho", "en", "klingon", ""] {
            XCTAssertTrue(p.requiresElderReview(isoLanguage: lang), "\(lang)")
        }
    }

    func testTheComplianceCataloguesAreNonEmpty() {
        XCTAssertTrue(DefaultComplianceCertifications().certifications.contains("ISO 27001"))
        XCTAssertTrue(DefaultPrivacyRegulationCompliance().laws.contains("POPIA"))
        XCTAssertTrue(DefaultGlobalRegulatorEngagement().activeJurisdictions.contains("ZA"))
        XCTAssertTrue(DefaultVerifiablePrivacyProof().buildIsReproducible)
    }

    func testTheFreeTierIsActuallyFree() {
        let free = DefaultPricingMatrix().all.first { $0.name == "free" }
        XCTAssertEqual(free?.monthlyPriceLocal, 0)
        XCTAssertEqual(free?.currency, "ZAR")
        XCTAssertFalse(free!.features.isEmpty, "free must still do something")
    }

    func testEveryTierIsPricedInTheSameCurrency() {
        let currencies = Set(DefaultPricingMatrix().all.map(\.currency))
        XCTAssertEqual(currencies, ["ZAR"])
        XCTAssertEqual(DefaultPricingMatrix().all.count, 5)
    }

    // Revenue has to exceed marginal cost or the free tier sinks the business.
    func testThePerUserMathIsSustainable() {
        let m = DefaultSustainablePerUserCostMath()
        XCTAssertGreaterThan(m.monthlyRevenuePerUser, m.monthlyMarginalCostPerUser)
    }

    func testTheAuthorKeepsMostOfAPluginSale() {
        XCTAssertGreaterThan(DefaultPluginMarketplaceRevenueShare().authorShare, 0.5)
    }

    // A symbol that changes with the phone locale turns R into $ on somebody
    // travelling, so the CODE is printed, invariantly.
    func testCurrencyFormattingIsInvariant() {
        let f = DefaultCurrencyFormatter()
        XCTAssertEqual(f.format(19, isoCurrencyCode: "ZAR"), "19.00 ZAR")
        XCTAssertEqual(f.format(Decimal(string: "3.456")!, isoCurrencyCode: "USD"), "3.46 USD")
        XCTAssertEqual(f.format(0, isoCurrencyCode: "NGN"), "0.00 NGN")
    }

    // Pretty-printing differs by country and getting it wrong makes a number
    // un-diallable, so E.164 is returned untouched.
    func testPhoneFormattingLeavesE164Alone() {
        XCTAssertEqual(DefaultPhoneNumberFormatter().format(e164: "+27825550142",
                                                            countryCodeIsoAlpha2: "ZA"),
                       "+27825550142")
    }

    func testGreetingsCoverBothCodeLengths() {
        let g = DefaultCulturalGreetings()
        XCTAssertEqual(g.greeting(for: "zul"), "Sawubona")
        XCTAssertEqual(g.greeting(for: "zu"), "Sawubona")
        XCTAssertEqual(g.greeting(for: "xho"), "Molo")
        XCTAssertEqual(g.greeting(for: "de"), "Hello", "an unknown language falls back")
    }

    func testNameRecognitionCoversTheSaLanguages() {
        let r = DefaultCulturalNameRecogniser()
        XCTAssertTrue(r.recognisesLanguage("zul"))
        XCTAssertTrue(r.recognisesLanguage("XHO"))
        XCTAssertFalse(r.recognisesLanguage("deu"))
    }

    // This has to run on the cheapest handset somebody can buy.
    func testTheHardwareFloorsAreLow() {
        XCTAssertTrue(DefaultLowRamPhoneSupport().supportsRamMb(512))
        XCTAssertFalse(DefaultLowRamPhoneSupport().supportsRamMb(256))
        XCTAssertTrue(DefaultLowCpuOptimization().supportsClockMhz(600))
        XCTAssertFalse(DefaultLowCpuOptimization().supportsClockMhz(400))
        XCTAssertTrue(DefaultKaiOsSupport().isCompiled, "a feature phone is still a phone")
    }

    // A full phone should lose caches, not the thing the person relies on.
    func testStorageDegradesCachesBeforeContent() {
        let order = DefaultStorageFullDegradationPolicy().degradeOrder
        XCTAssertLessThan(order.range(of: "cache")!.lowerBound,
                          order.range(of: "chat-history")!.lowerBound)
    }

    func testTheDistributionChannelsAreListed() {
        XCTAssertTrue(DefaultSideloadChannel().formats.contains("APK"))
        XCTAssertTrue(DefaultLinuxRepoFanout().repos.contains("flatpak"))
        XCTAssertTrue(DefaultPwaFallback().pwaUrl.hasPrefix("https://"))
    }

    // IMAP is the escape hatch for every provider not on the list.
    func testTheEmailRegistryHasAGenericFallback() {
        XCTAssertTrue(DefaultEmailConnectorRegistry().providers.contains("IMAP"))
        XCTAssertTrue(DefaultCalendarConnectorRegistry().providers.contains("CalDAV"))
    }

    func testTheConnectorRegistriesAreNonEmpty() {
        XCTAssertFalse(DefaultCrmConnectorRegistry().providers.isEmpty)
        XCTAssertFalse(DefaultAccountingConnectorRegistry().providers.isEmpty)
        XCTAssertFalse(DefaultBankingConnectorRegistry().providers.isEmpty)
        XCTAssertFalse(DefaultSaServiceConnectors().banks.isEmpty)
        XCTAssertFalse(DefaultCrossBorderCorridors().corridors.isEmpty)
    }

    func testOnDeviceRoutingIsPreferred() {
        XCTAssertTrue(DefaultLocalFirstRouting().preferred)
        XCTAssertTrue(DefaultBrainUnreachableMode().localTakeoverEnabled)
        XCTAssertGreaterThan(DefaultNoInternetCacheTarget().hitRateTarget, 0.5)
    }
}
