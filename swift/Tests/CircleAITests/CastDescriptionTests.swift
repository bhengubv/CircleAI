import XCTest
@testable import CircleAI

/// Reading a UPnP device description.
final class CastDescriptionTests: XCTestCase {

    private let location = URL(string: "http://192.168.1.50:8080/dev/description.xml")!

    private func doc(service: String = "urn:schemas-upnp-org:service:AVTransport:1",
                     control: String = "/AVTransport/control",
                     urlBase: String? = nil,
                     icon: String? = "/icon/sm.png") -> String {
        let base = urlBase.map { "<URLBase>\($0)</URLBase>" } ?? ""
        let iconXml = icon.map { "<iconList><icon><mimetype>image/png</mimetype><url>\($0)</url></icon></iconList>" } ?? ""
        return """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          \(base)
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>Lounge TV</friendlyName>
            <manufacturer>Acme</manufacturer>
            <modelName>SmartBox 400</modelName>
            <UDN>uuid:abc-123</UDN>
            \(iconXml)
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <controlURL>/CM/control</controlURL>
              </service>
              <service>
                <serviceType>\(service)</serviceType>
                <controlURL>\(control)</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """
    }

    func testAFullDescriptionIsRead() {
        let d = DeviceDescription.parse(doc(), location: location)
        XCTAssertEqual(d?.friendlyName, "Lounge TV")
        XCTAssertEqual(d?.manufacturer, "Acme")
        XCTAssertEqual(d?.modelName, "SmartBox 400")
        XCTAssertEqual(d?.udn, "uuid:abc-123")
    }

    // The AVTransport service must be picked, not the first service listed.
    func testTheAvTransportControlUrlIsTheOneChosen() {
        let d = DeviceDescription.parse(doc(), location: location)
        XCTAssertEqual(d?.avTransportControlUrl.absoluteString,
                       "http://192.168.1.50:8080/AVTransport/control")
    }

    // A renderer with no AVTransport cannot be controlled, so it is not a target.
    func testADeviceWithoutAvTransportIsNotATarget() {
        let d = DeviceDescription.parse(
            doc(service: "urn:schemas-upnp-org:service:RenderingControl:1"), location: location)
        XCTAssertNil(d)
    }

    func testAnEmptyControlUrlIsRefused() {
        XCTAssertNil(DeviceDescription.parse(doc(control: "   "), location: location))
    }

    func testUrlBaseWinsOverTheDescriptionLocation() {
        let d = DeviceDescription.parse(doc(urlBase: "http://10.0.0.9:2020/"), location: location)
        XCTAssertEqual(d?.avTransportControlUrl.absoluteString,
                       "http://10.0.0.9:2020/AVTransport/control")
    }

    // A relative control path resolves against the DIRECTORY of the
    // description, not against the origin.
    func testARelativeControlPathResolvesAgainstTheDescriptionDirectory() {
        let d = DeviceDescription.parse(doc(control: "control"), location: location)
        XCTAssertEqual(d?.avTransportControlUrl.absoluteString,
                       "http://192.168.1.50:8080/dev/control")
    }

    func testAnAbsoluteControlUrlIsUsedAsIs() {
        let d = DeviceDescription.parse(doc(control: "http://1.2.3.4/x"), location: location)
        XCTAssertEqual(d?.avTransportControlUrl.absoluteString, "http://1.2.3.4/x")
    }

    func testTheIconIsResolvedWhenPresentAndNilWhenNot() {
        XCTAssertEqual(DeviceDescription.parse(doc(), location: location)?.iconUrl?.absoluteString,
                       "http://192.168.1.50:8080/icon/sm.png")
        XCTAssertNil(DeviceDescription.parse(doc(icon: nil), location: location)?.iconUrl)
    }

    // A device with no name still has to be showable in a list.
    func testAnUnnamedDeviceFallsBackToSomethingPrintable() {
        let xml = """
        <?xml version="1.0"?>
        <root><device>
          <serviceList><service>
            <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
            <controlURL>/c</controlURL>
          </service></serviceList>
        </device></root>
        """
        let d = DeviceDescription.parse(xml, location: location)
        XCTAssertEqual(d?.friendlyName, "DLNA Renderer")
        XCTAssertEqual(d?.udn, location.absoluteString)
    }

    // Broken XML is a device to skip, not a crash.
    func testMalformedXmlIsNil() {
        XCTAssertNil(DeviceDescription.parse("<root><device>", location: location))
        XCTAssertNil(DeviceDescription.parse("", location: location))
    }

    func testADescribedTargetExposesWhatWasDiscovered() async {
        let d = DeviceDescription.parse(doc(), location: location)!
        let t = DescribedCastTarget(d)
        XCTAssertEqual(t.id, CastTargetId("uuid:abc-123"))
        XCTAssertEqual(t.friendlyName, "Lounge TV")
        XCTAssertEqual(t.castProtocol, .dlna)

        // ...and admits it cannot connect on a build with no transport.
        do {
            _ = try await t.connect()
            XCTFail("expected a refusal")
        } catch let e as CastError {
            XCTAssertTrue(e.description.contains("Lounge TV"))
        } catch { XCTFail("wrong error") }
    }

    func testTheNullDiscoveryFindsNothing() async {
        var found = 0
        for await _ in NullCastDiscovery.instance.discover(searchWindow: 1) { found += 1 }
        XCTAssertEqual(found, 0)
    }
}
