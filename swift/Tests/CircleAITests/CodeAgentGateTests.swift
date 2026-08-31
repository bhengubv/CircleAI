import XCTest
@testable import CircleAI

/// The gate, the catalogue, the path guard and the runner refusal - the four
/// places this module says "no" and has to mean it.
final class CodeAgentGateTests: XCTestCase {

    private func probe(ramGb: Double, storageGb: Double, cores: Int = 8,
                       thermal: ThermalClass = .active) -> DeviceProbe {
        DeviceProbe(
            ramAvailableBytes: Int64(ramGb * 1024 * 1024 * 1024),
            storageFreeBytes: Int64(storageGb * 1024 * 1024 * 1024),
            cpuCores: cores,
            thermalClass: thermal)
    }

    private func model(_ id: String, b: Int, ram: Double = 6, storage: Double = 4,
                       caps: ChatCapability = [.tools, .reasoning, .longContext]) -> CodingModelDescriptor {
        CodingModelDescriptor(modelId: id, parametersBillion: b, minRamGb: ram,
                              minFreeStorageGb: storage, totalBytes: 4_000_000_000,
                              sha256: "abc123", capabilities: caps)
    }

    // MARK: - The device gate

    func testAPhoneIsRefusedByDesign() {
        let plan = CodingCapabilityPlanner().planForCoding(probe(ramGb: 1.5, storageGb: 40))
        XCTAssertFalse(plan.isAvailable)
        XCTAssertEqual(plan.quality, .unavailable)
        XCTAssertTrue(plan.reason.contains("Unavailable by design"))
    }

    func testACapableDeviceWithNoCatalogueSaysSoInsteadOfPretending() {
        let plan = CodingCapabilityPlanner().planForCoding(probe(ramGb: 16, storageGb: 100))
        XCTAssertFalse(plan.isAvailable)
        XCTAssertTrue(plan.reason.contains("no on-device coding model is installed"))
    }

    func testThinStorageIsRefusedEvenWithPlentyOfRam() throws {
        let cat = try InMemoryCodingModelCatalog(seed: [model("m", b: 7)])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 16, storageGb: 2))
        XCTAssertFalse(plan.isAvailable)
        XCTAssertTrue(plan.reason.contains("free storage"))
    }

    func testACapableDeviceWithAFittingModelPasses() throws {
        let cat = try InMemoryCodingModelCatalog(seed: [model("qwen-coder-7b", b: 7)])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 16, storageGb: 100))
        XCTAssertTrue(plan.isAvailable)
        XCTAssertEqual(plan.quality, .good)
        XCTAssertEqual(plan.model?.modelId, "qwen-coder-7b")
    }

    func testTheBiggestFittingModelWins() throws {
        let cat = try InMemoryCodingModelCatalog(seed: [model("small-3b", b: 3), model("big-7b", b: 7)])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 16, storageGb: 100))
        XCTAssertEqual(plan.model?.modelId, "big-7b")
    }

    func testAModelMissingARequiredCapabilityDoesNotFit() throws {
        let cat = try InMemoryCodingModelCatalog(seed: [model("no-tools-7b", b: 7, caps: [.reasoning, .longContext])])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 16, storageGb: 100))
        XCTAssertEqual(plan.quality, .nothingFits)
    }

    func testATooSmallModelDoesNotFit() throws {
        let cat = try InMemoryCodingModelCatalog(seed: [model("tiny-1b", b: 1)])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 16, storageGb: 100))
        XCTAssertEqual(plan.quality, .nothingFits)
    }

    // The headroom is the point: 85% of free RAM is what a model may claim.
    func testRamFitUsesTheHeadroomNotTheRawFreeFigure() throws {
        // 10 GiB free -> 10.7 GB decimal -> 9.1 GB usable after headroom.
        let cat = try InMemoryCodingModelCatalog(seed: [model("needs-10", b: 7, ram: 10)])
        let plan = CodingCapabilityPlanner(catalog: cat).planForCoding(probe(ramGb: 10, storageGb: 100))
        XCTAssertEqual(plan.quality, .nothingFits)
    }

    // MARK: - The catalogue

    func testAModelWithoutAHashIsRefused() {
        let bad = CodingModelDescriptor(modelId: "unverified", parametersBillion: 7, minRamGb: 6,
                                        minFreeStorageGb: 4, totalBytes: 1, sha256: "  ",
                                        capabilities: [.tools])
        XCTAssertThrowsError(try InMemoryCodingModelCatalog().add(bad)) { error in
            XCTAssertEqual(error as? CodingCatalogError, .unverifiable("unverified"))
        }
    }

    func testAddingTheSameModelTwiceIsIdempotent() throws {
        let cat = try InMemoryCodingModelCatalog()
        try cat.add(model("m", b: 7))
        try cat.add(model("M", b: 7))
        XCTAssertEqual(cat.available.count, 1)
    }

    func testAnEmptyCatalogueIsEmpty() {
        XCTAssertTrue(EmptyCodingModelCatalog.instance.available.isEmpty)
        XCTAssertEqual(EmptyCodingModelCatalog.instance.backendId, "empty")
    }
}
