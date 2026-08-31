// DocumentsTests.swift
//
// Behaviour checks for the Documents port. The money maths is the part worth
// testing hardest: it is the only place in this module where getting the port
// subtly wrong costs somebody real money.

import XCTest
@testable import CircleAI

final class DocumentsTests: XCTestCase {

    // MARK: - Contracts

    func test_document_kind_raw_values_are_stable() {
        XCTAssertEqual(DocumentKind.cv.rawValue, 0)
        XCTAssertEqual(DocumentKind.coverLetter.rawValue, 1)
        XCTAssertEqual(DocumentKind.invoice.rawValue, 2)
        XCTAssertEqual(DocumentKind.report.rawValue, 3)
    }

    func test_pdf_result_carries_the_standard_mime_type() {
        let r = DocumentResult.pdf(Data([1, 2, 3]), suggestedFileName: "Thabo-CV.pdf")
        XCTAssertEqual(r.mimeType, "application/pdf")
        XCTAssertEqual(r.suggestedFileName, "Thabo-CV.pdf")
        XCTAssertEqual(r.bytes.count, 3)
    }

    func test_request_defaults_to_pdf_and_the_default_template() {
        let req = DocumentRequest(kind: .cv, model: CvDocument.minimal(
            fullName: "A", headline: "B", contact: CvContact()))
        XCTAssertEqual(req.format, .pdf)
        XCTAssertNil(req.templateId, "nil means the engine picks its default for the kind")
    }

    // MARK: - CV

    func test_minimal_cv_is_empty_but_valid() {
        let cv = CvDocument.minimal(fullName: "Thabo Mokoena", headline: "Developer",
                                    contact: CvContact(email: "t@example.co.za"))
        XCTAssertEqual(cv.fullName, "Thabo Mokoena")
        XCTAssertTrue(cv.experience.isEmpty)
        XCTAssertTrue(cv.education.isEmpty)
        XCTAssertTrue(cv.skills.isEmpty)
        XCTAssertNil(cv.summary)
        XCTAssertNil(cv.certifications, "absent, not empty - the C# leaves it null")
    }

    // MARK: - Cover letter derivations

    func test_greeting_prefers_the_explicit_one() {
        let l = CoverLetter(senderName: "A", senderContact: CvContact(), date: "d",
                            recipientName: "Ms Dlamini", recipientCompany: "Aurora",
                            subject: "s", greeting: "Hi Nomsa,")
        XCTAssertEqual(l.effectiveGreeting, "Hi Nomsa,")
    }

    func test_greeting_is_derived_from_the_recipient_when_absent() {
        let l = CoverLetter(senderName: "A", senderContact: CvContact(), date: "d",
                            recipientName: "Ms Dlamini", recipientCompany: "Aurora", subject: "s")
        XCTAssertEqual(l.effectiveGreeting, "Dear Ms Dlamini,")
    }

    func test_greeting_falls_back_to_the_formal_form_with_no_recipient() {
        let l = CoverLetter(senderName: "A", senderContact: CvContact(), date: "d",
                            recipientCompany: "Aurora", subject: "s")
        XCTAssertEqual(l.effectiveGreeting, "Dear Sir or Madam,")
    }

    func test_whitespace_is_treated_as_absent_not_as_a_value() {
        // The C# uses IsNullOrWhiteSpace, so "   " must fall through to the
        // derived greeting rather than printing three spaces on the letter.
        let l = CoverLetter(senderName: "Thabo", senderContact: CvContact(), date: "d",
                            recipientName: "Ms Dlamini", recipientCompany: "Aurora",
                            subject: "s", greeting: "   ", closing: "  ", signatureName: " ")
        XCTAssertEqual(l.effectiveGreeting, "Dear Ms Dlamini,")
        XCTAssertEqual(l.effectiveClosing, "Yours sincerely,")
        XCTAssertEqual(l.effectiveSignature, "Thabo")
    }

    // MARK: - Invoice money maths

    private func item(_ q: Decimal, _ p: Decimal) -> InvoiceLineItem {
        InvoiceLineItem(description: "x", quantity: q, unitPrice: p)
    }

    private func invoice(_ items: [InvoiceLineItem], vat: Decimal) -> InvoiceDocument {
        InvoiceDocument(from: InvoiceParty(name: "F"), to: InvoiceParty(name: "T"),
                        invoiceNumber: "1", issueDate: "d", dueDate: "d",
                        lineItems: items, vatPercent: vat)
    }

    func test_line_total_is_quantity_times_unit_price() {
        XCTAssertEqual(item(4.5, 650).lineTotal, Decimal(string: "2925")!)
    }

    func test_subtotal_vat_and_total_agree_with_the_reference() {
        let inv = invoice([item(1, Decimal(string: "6500.00")!),
                           item(Decimal(string: "4.5")!, 650),
                           item(3, 850)], vat: 15)
        XCTAssertEqual(inv.subtotal, Decimal(string: "11975")!)
        XCTAssertEqual(inv.vatAmount, Decimal(string: "1796.25")!)
        XCTAssertEqual(inv.total, Decimal(string: "13771.25")!)
    }

    func test_rounding_is_half_up_not_bankers() {
        // Foundation rounds to even by default: 2.125 -> 2.12, and 2.135 -> 2.14.
        // The C# passes MidpointRounding.AwayFromZero, so both must go up.
        XCTAssertEqual(InvoiceDocument.round2(Decimal(string: "2.125")!), Decimal(string: "2.13")!)
        XCTAssertEqual(InvoiceDocument.round2(Decimal(string: "2.135")!), Decimal(string: "2.14")!)
    }

    func test_each_line_is_rounded_before_summing() {
        // Two lines of 0.005 round to 0.01 EACH (0.02 total). Summing first would
        // give 0.01. The invoice must agree with the numbers a person can add up
        // off the page.
        let inv = invoice([item(1, Decimal(string: "0.005")!),
                           item(1, Decimal(string: "0.005")!)], vat: 0)
        XCTAssertEqual(inv.subtotal, Decimal(string: "0.02")!)
    }

    func test_zero_vat_leaves_the_total_equal_to_the_subtotal() {
        let inv = invoice([item(2, 100)], vat: 0)
        XCTAssertEqual(inv.vatAmount, 0)
        XCTAssertEqual(inv.total, inv.subtotal)
    }

    func test_an_invoice_with_no_lines_totals_zero_rather_than_failing() {
        let inv = invoice([], vat: 15)
        XCTAssertEqual(inv.subtotal, 0)
        XCTAssertEqual(inv.total, 0)
    }

    func test_currency_defaults_to_zar() {
        XCTAssertEqual(InvoiceDocument.minimal(
            from: InvoiceParty(name: "F"), to: InvoiceParty(name: "T"),
            number: "1", issueDate: "d", dueDate: "d").currencyCode, "ZAR")
    }

    // MARK: - Report

    func test_report_sections_may_carry_any_combination_of_content() {
        let s = ReportSection(heading: "H", bullets: ["a", "b"])
        XCTAssertNil(s.paragraphs)
        XCTAssertNil(s.table)
        XCTAssertEqual(s.bullets?.count, 2)
    }

    func test_minimal_report_has_a_title_and_nothing_else() {
        let r = ReportDocument.minimal(title: "Pilot Report")
        XCTAssertEqual(r.title, "Pilot Report")
        XCTAssertTrue(r.sections.isEmpty)
        XCTAssertNil(r.author)
    }
}
