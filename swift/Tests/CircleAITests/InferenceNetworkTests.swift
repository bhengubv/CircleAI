// InferenceNetworkTests.swift

import XCTest
@testable import CircleAI

final class InferenceNetworkTests: XCTestCase {

    private func urlError(_ code: Int, _ message: String = "failed") -> NSError {
        NSError(domain: NSURLErrorDomain, code: code,
                userInfo: [NSLocalizedDescriptionKey: message])
    }

    // MARK: - Classification

    func testAllTheseFaultsAreDistinguishedRatherThanLumpedTogether() {
        // The whole reason this file exists: "the mirror is down", "you are
        // offline", "the hotel wifi wants you to log in" and "the file 404'd"
        // have completely different remedies and used to read identically.
        XCTAssertEqual(NetworkDiagnosis.classify(
            urlError(NSURLErrorNotConnectedToInternet)).fault, .noLink)
        XCTAssertEqual(NetworkDiagnosis.classify(
            urlError(NSURLErrorTimedOut)).fault, .timeout)
        XCTAssertEqual(NetworkDiagnosis.classify(
            urlError(NSURLErrorCannotConnectToHost)).fault, .hostUnreachable)
        XCTAssertEqual(NetworkDiagnosis.classify(
            urlError(NSURLErrorSecureConnectionFailed)).fault, .tlsFailure)
        XCTAssertEqual(NetworkDiagnosis.classify(httpStatus: 404).fault, .httpError)
    }

    func testTheAndroidResolverFailureIsMatchedByNameAndText() {
        // On Android the failure is a Java type that does not exist in a
        // portable library and cannot be caught by type at all. This is the
        // exact string the P30 produced.
        let e = NSError(domain: "Java.Net", code: 0, userInfo: [
            NSLocalizedDescriptionKey:
                "Java.Net.UnknownHostException: Unable to resolve host \"modelscope.cn\""])
        let d = NetworkDiagnosis.classify(e)
        XCTAssertEqual(d.fault, .dnsFailure)
        XCTAssertTrue(d.isTransient)
        XCTAssertFalse(d.remedy.isEmpty, "the user CAN fix this one")
    }

    func testEverySpellingOfNameResolutionFailureIsCaught() {
        let spellings = [
            "Unable to resolve host \"x.com\"",
            "No address associated with hostname",
            "EAI_NODATA",
            "EAI_NONAME",
            "Name or service not known",
            "nodename nor servname provided, or not known",
        ]
        for s in spellings {
            let e = NSError(domain: "test", code: 0, userInfo: [NSLocalizedDescriptionKey: s])
            XCTAssertEqual(NetworkDiagnosis.classify(e).fault, .dnsFailure, s)
        }
    }

    func testDnsIsCheckedBeforeTheGenericConnectionArm() {
        // A resolution failure IS also a connection failure. Checking the
        // generic case first reports "no network" to somebody whose network is
        // fine and whose resolver is not - and then they reboot a working router.
        let e = NSError(domain: NSURLErrorDomain, code: NSURLErrorDNSLookupFailed,
                        userInfo: [NSLocalizedDescriptionKey: "A server with the specified hostname could not be found."])
        XCTAssertEqual(NetworkDiagnosis.classify(e).fault, .dnsFailure)
    }

    func testAnUnderlyingErrorIsReachedThroughTheWrapper() {
        // The real cause is nearly always wrapped by the time it surfaces.
        let inner = NSError(domain: "Java.Net", code: 0, userInfo: [
            NSLocalizedDescriptionKey: "UnknownHostException: Unable to resolve host"])
        let outer = NSError(domain: "Http", code: 0, userInfo: [
            NSLocalizedDescriptionKey: "Connection failure",
            NSUnderlyingErrorKey: inner])
        XCTAssertEqual(NetworkDiagnosis.classify(outer).fault, .dnsFailure)
    }

    func testADeadMirrorOffersNoRemedyBecauseItIsNotTheUsersToFix() {
        // Inventing "check your connection" for a problem on our side sends
        // somebody to reboot a router that was working.
        let d = NetworkDiagnosis.classify(urlError(NSURLErrorCannotConnectToHost))
        XCTAssertTrue(d.remedy.isEmpty)
        XCTAssertTrue(d.isTransient)
    }

    func testAnUnclassifiableErrorIsUnknownAndTransientNotHealthy() {
        struct Odd: Error {}
        let d = NetworkDiagnosis.classify(Odd())
        XCTAssertEqual(d.fault, .unknown)
        XCTAssertTrue(d.isTransient)
        XCTAssertTrue(d.shouldBlockDownload)
    }

    // MARK: - Retry policy

    func testFiveHundredsAndRateLimitsAreWorthRetryingAndFourHundredsAreNot() {
        // Spinning on a 404 wastes battery and never succeeds.
        XCTAssertTrue(NetworkDiagnosis.classify(httpStatus: 500).isTransient)
        XCTAssertTrue(NetworkDiagnosis.classify(httpStatus: 503).isTransient)
        XCTAssertTrue(NetworkDiagnosis.classify(httpStatus: 429).isTransient)
        XCTAssertFalse(NetworkDiagnosis.classify(httpStatus: 404).isTransient)
        XCTAssertFalse(NetworkDiagnosis.classify(httpStatus: 401).isTransient)
        XCTAssertFalse(NetworkDiagnosis.classify(httpStatus: 403).isTransient)
    }

    func testASuccessStatusIsHealthyAndBlocksNothing() {
        for code in [200, 204, 206, 299] {
            let d = NetworkDiagnosis.classify(httpStatus: code)
            XCTAssertEqual(d.fault, .none, "\(code)")
            XCTAssertFalse(d.shouldBlockDownload)
        }
    }

    func testHealthyIsTheOnlyFaultThatDoesNotBlock() {
        XCTAssertFalse(NetworkDiagnosis.healthy.shouldBlockDownload)
        XCTAssertEqual(NetworkDiagnosis.healthy.description, "network: ok")
        for fault in NetworkFault.allCases where fault != .none {
            XCTAssertTrue(NetworkDiagnosis(fault: fault, detail: "", remedy: "",
                                           isTransient: true).shouldBlockDownload)
        }
    }

    func testTheDescriptionIncludesTheRemedyWhenThereIsOne() {
        let withRemedy = NetworkDiagnosis(fault: .dnsFailure, detail: "no dns",
                                          remedy: "Toggle Wi-Fi.", isTransient: true)
        XCTAssertTrue(withRemedy.description.contains("Toggle Wi-Fi."))

        let without = NetworkDiagnosis(fault: .hostUnreachable, detail: "dead",
                                       remedy: "", isTransient: true)
        XCTAssertFalse(without.description.hasSuffix(". "))
        XCTAssertTrue(without.description.contains("dead"))
    }

    // MARK: - The failure a caller receives

    func testTheDownloadFailureCarriesItsDiagnosis() {
        let d = NetworkDiagnosis.classify(urlError(NSURLErrorNotConnectedToInternet))
        let e = ModelDownloadError.diagnosed(message: "modelscope.cn", diagnosis: d)
        guard case .diagnosed(_, let carried) = e else { return XCTFail() }
        XCTAssertEqual(carried.fault, .noLink)
        XCTAssertTrue(e.description.contains("modelscope.cn"))
    }

    func testTheUserMessageIsTheRemedyWhenThereIsOneAndPlainWhenThereIsNot() {
        // "Unable to resolve host modelscope.cn" tells somebody holding a phone
        // nothing they can act on.
        let fixable = ModelDownloadError.diagnosed(
            message: "x", diagnosis: NetworkDiagnosis.classify(
                NSError(domain: "t", code: 0,
                        userInfo: [NSLocalizedDescriptionKey: "Unable to resolve host"])))
        XCTAssertTrue(fixable.userMessage.contains("Wi-Fi"))

        let notFixable = ModelDownloadError.diagnosed(
            message: "x", diagnosis: NetworkDiagnosis.classify(
                urlError(NSURLErrorCannotConnectToHost)))
        XCTAssertEqual(notFixable.userMessage,
                       "The model could not be downloaded right now. Please try again later.")
    }

    func testTheOtherDownloadErrorsStillHaveAPlainUserMessage() {
        XCTAssertFalse(ModelDownloadError.shaMismatch("bad hash").userMessage.isEmpty)
        XCTAssertFalse(ModelDownloadError.httpStatus(500).userMessage.isEmpty)
    }

    // MARK: - The Wi-Fi-only gate

    private func gate(_ net: String?, wifiOnly: Bool = true) -> MeteredNetworkDownloadGate {
        MeteredNetworkDownloadGate(networkType: { net }, wifiOnly: wifiOnly)
    }

    func testMobileDataIsBlockedWithTheSizeInTheMessage() {
        // The smallest catalogued bundle is 433 MB, which is real money on a
        // South African prepaid bundle.
        let reason = gate("cellular").blockReason(estimatedBytes: 433 * 1024 * 1024)
        XCTAssertNotNil(reason)
        XCTAssertTrue(reason!.contains("433 MB"))
        XCTAssertTrue(reason!.contains("mobile data"))
    }

    func testEverySpellingOfMeteredIsBlocked() {
        for net in ["cellular", "mobile", "metered", "CELLULAR", " Mobile "] {
            XCTAssertNotNil(gate(net).blockReason(estimatedBytes: 1),
                            "\(net) should be blocked")
        }
    }

    func testUnmeteredLinksAreAllowed() {
        for net in ["wifi", "ethernet", "unmetered", "WiFi"] {
            XCTAssertNil(gate(net).blockReason(estimatedBytes: 1), "\(net) should be allowed")
        }
    }

    func testNoNetworkIsBlockedWithItsOwnMessage() {
        let reason = gate("none").blockReason(estimatedBytes: 1)
        XCTAssertNotNil(reason)
        XCTAssertTrue(reason!.contains("No network"))
    }

    func testAnUnknownSizeStillProducesAReadableMessage() {
        let reason = gate("cellular").blockReason(estimatedBytes: 0)
        XCTAssertNotNil(reason)
        XCTAssertTrue(reason!.contains("a large"))
        XCTAssertFalse(reason!.contains("0 MB"))
    }

    // MARK: - The honest difficulty

    func testAHostThatCannotTellMeteredFromUnmeteredFailsOpenButNotSilently() {
        // Failing CLOSED on "online" would stop every desktop host downloading
        // anything; failing OPEN silently recreates the original bug on exactly
        // the devices this was meant to protect. So: open, and say so.
        let g = gate("online")
        XCTAssertNil(g.blockReason(estimatedBytes: 500_000_000), "fails open")
        XCTAssertFalse(g.isEnforceable, "and admits it could not check")
    }

    func testANilNetworkTypeIsAlsoUnenforceableRatherThanAssumedSafe() {
        let g = gate(nil)
        XCTAssertNil(g.blockReason(estimatedBytes: 1))
        XCTAssertFalse(g.isEnforceable)
    }

    func testAMeshLinkIsAllowedButNotClaimedAsVerified() {
        let g = gate("mesh")
        XCTAssertNil(g.blockReason(estimatedBytes: 1))
        XCTAssertFalse(g.isEnforceable)
    }

    func testARealMobileHostGetsRealEnforcement() {
        XCTAssertTrue(gate("wifi").isEnforceable)
        XCTAssertTrue(gate("cellular").isEnforceable)
        XCTAssertTrue(gate("none").isEnforceable)
    }

    func testTurningTheOptionOffAllowsEverythingAndIsFullyEnforceable() {
        // Nothing to enforce is not the same as unable to enforce.
        let g = gate("cellular", wifiOnly: false)
        XCTAssertNil(g.blockReason(estimatedBytes: 900_000_000))
        XCTAssertTrue(g.isEnforceable)
    }

    func testTheGateReadsADeviceContextWhenGivenOne() {
        // The default context can only say "online", which is precisely the
        // unenforceable case.
        let g = MeteredNetworkDownloadGate(device: SystemInfoDeviceContext())
        XCTAssertFalse(g.isEnforceable)
        XCTAssertNil(g.blockReason(estimatedBytes: 500_000_000))
    }
}
