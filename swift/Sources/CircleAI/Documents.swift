// Documents.swift
//
// Port of src/CircleAI.Documents/:
//   • DocumentContracts.cs → DocumentKind, DocumentFormat, DocumentRequest,
//                            DocumentResult
//   • IDocumentEngine.cs   → DocumentEngine
//   • CvDocument.cs        → CvDocument, CvContact, CvExperience, CvEducation,
//                            CvCertification
//   • CoverLetter.cs       → CoverLetter (+ the three effective* derivations)
//   • Invoice.cs           → InvoiceParty, InvoiceLineItem, InvoiceDocument (+ money maths)
//   • ReportDocument.cs    → ReportDocument, ReportSection, ReportTable
//
// Porting notes:
//   • The MODEL and the SEAM come across; the PDFsharp templates do not. The C#
//     header calls the contracts "renderer-agnostic … nothing here knows whether
//     the bytes are produced by PDFsharp, QuestPDF, or a future HTML path", and
//     that is exactly the half that ports. SingleColumnCvTemplate and friends are
//     PDFsharp drawing code with no Swift counterpart.
//
//   • DocumentRequest.Model is `object` in C# so one non-generic engine can carry
//     every kind. Swift gets `DocumentModel`, a marker protocol the four models
//     conform to - same freedom, but a host cannot hand the engine a String by
//     accident.
//
//   • MONEY IS Decimal, NOT Double. C# uses `decimal` for invoice amounts and
//     rounds away from zero to 2 places. Foundation's Decimal is the only Swift
//     type with the same base-10 behaviour; Double would make 0.1 + 0.2 visible
//     on somebody's invoice.
//
//   • `ValueTask<DocumentResult>` → `async throws -> DocumentResult`.

import Foundation

// MARK: - Contracts

/// What kind of document is being produced.
///
/// Every kind rides the SAME engine - a CV and an invoice differ only in their
/// model and template, not in the pipeline.
public enum DocumentKind: Int, Sendable, Equatable, CaseIterable {
    /// A curriculum vitae / resume. The confirmed floor.
    case cv = 0
    /// A cover letter (same engine, different template).
    case coverLetter
    /// An invoice.
    case invoice
    /// A report.
    case report
}

/// Output container format.
///
/// PDF only for v1 - it is the format a person can open and send from a phone.
/// The enum exists so DOCX/HTML can be added later without changing the seam.
public enum DocumentFormat: Int, Sendable, Equatable, CaseIterable {
    case pdf = 0
}

/// A typed content model one of the templates can lay out.
///
/// Stands in for the C# `object` on `DocumentRequest.Model`, which exists so the
/// engine can stay non-generic across every kind. A marker protocol keeps that
/// freedom while making "the engine validates the type and throws a clear error
/// on a mismatch" mostly a compile-time matter instead.
public protocol DocumentModel: Sendable {}

/// A request to render one document.
public struct DocumentRequest: Sendable {
    /// Which document - determines which template renders it.
    public let kind: DocumentKind
    /// The typed content for this kind; must match `kind`.
    public let model: any DocumentModel
    /// Which template to use; nil selects the engine's default for the kind.
    public let templateId: String?
    /// Output format.
    public let format: DocumentFormat

    public init(
        kind: DocumentKind,
        model: any DocumentModel,
        templateId: String? = nil,
        format: DocumentFormat = .pdf
    ) {
        self.kind = kind
        self.model = model
        self.templateId = templateId
        self.format = format
    }
}

/// A rendered document, as bytes.
///
/// Bytes, not a file path, on purpose: WHERE the document lands is a
/// platform-specific decision the host owns. The engine stays platform-neutral
/// and never touches the filesystem.
public struct DocumentResult: Sendable, Equatable {
    /// The rendered document.
    public let bytes: Data
    /// e.g. `application/pdf` - for the share/open intent.
    public let mimeType: String
    /// e.g. `Thabo-Mokoena-CV.pdf`. A suggestion; the host may override.
    public let suggestedFileName: String

    public init(bytes: Data, mimeType: String, suggestedFileName: String) {
        self.bytes = bytes
        self.mimeType = mimeType
        self.suggestedFileName = suggestedFileName
    }

    /// Builds a PDF result with the standard MIME type.
    public static func pdf(_ bytes: Data, suggestedFileName: String) -> DocumentResult {
        DocumentResult(bytes: bytes, mimeType: "application/pdf", suggestedFileName: suggestedFileName)
    }
}

/// Renders a `DocumentRequest` to bytes, fully offline and on-device.
public protocol DocumentEngine: Sendable {
    func render(_ request: DocumentRequest) async throws -> DocumentResult
    /// Template ids this engine can render. The first is the default.
    var availableTemplates: [String] { get }
}

/// Raised when a request's model does not match its kind.
///
/// The C# engine "validates the type and throws a clear error on a mismatch
/// rather than rendering garbage"; this is that error, named.
public struct DocumentModelMismatch: Error, Sendable, Equatable {
    public let kind: DocumentKind
    public let received: String

    public init(kind: DocumentKind, received: String) {
        self.kind = kind
        self.received = received
    }
}

// MARK: - CV

/// A curriculum vitae. Also a clean JSON target for a model filling one in.
public struct CvDocument: DocumentModel, Equatable {
    public let fullName: String
    public let headline: String
    public let contact: CvContact
    public let summary: String?
    public let experience: [CvExperience]
    public let education: [CvEducation]
    public let skills: [String]
    public let certifications: [CvCertification]?
    public let languages: [String]?

    public init(
        fullName: String,
        headline: String,
        contact: CvContact,
        summary: String? = nil,
        experience: [CvExperience] = [],
        education: [CvEducation] = [],
        skills: [String] = [],
        certifications: [CvCertification]? = nil,
        languages: [String]? = nil
    ) {
        self.fullName = fullName
        self.headline = headline
        self.contact = contact
        self.summary = summary
        self.experience = experience
        self.education = education
        self.skills = skills
        self.certifications = certifications
        self.languages = languages
    }

    /// The smallest CV the engine will render: a name, a headline, a way to reach you.
    public static func minimal(fullName: String, headline: String, contact: CvContact) -> CvDocument {
        CvDocument(fullName: fullName, headline: headline, contact: contact)
    }
}

public struct CvContact: Sendable, Equatable {
    public let email: String?
    public let phone: String?
    public let location: String?
    public let links: [String]?

    public init(email: String? = nil, phone: String? = nil, location: String? = nil, links: [String]? = nil) {
        self.email = email
        self.phone = phone
        self.location = location
        self.links = links
    }
}

public struct CvExperience: Sendable, Equatable {
    public let title: String
    public let organisation: String
    public let location: String?
    public let startDate: String
    public let endDate: String?
    public let highlights: [String]

    public init(title: String, organisation: String, location: String? = nil,
                startDate: String, endDate: String? = nil, highlights: [String] = []) {
        self.title = title
        self.organisation = organisation
        self.location = location
        self.startDate = startDate
        self.endDate = endDate
        self.highlights = highlights
    }
}

public struct CvEducation: Sendable, Equatable {
    public let qualification: String
    public let institution: String
    public let location: String?
    public let startDate: String?
    public let endDate: String?
    public let notes: String?

    public init(qualification: String, institution: String, location: String? = nil,
                startDate: String? = nil, endDate: String? = nil, notes: String? = nil) {
        self.qualification = qualification
        self.institution = institution
        self.location = location
        self.startDate = startDate
        self.endDate = endDate
        self.notes = notes
    }
}

public struct CvCertification: Sendable, Equatable {
    public let name: String
    public let issuer: String?
    public let year: String?

    public init(name: String, issuer: String? = nil, year: String? = nil) {
        self.name = name
        self.issuer = issuer
        self.year = year
    }
}

// MARK: - Cover letter

public struct CoverLetter: DocumentModel, Equatable {
    public let senderName: String
    public let senderContact: CvContact
    public let date: String
    public let recipientName: String?
    public let recipientTitle: String?
    public let recipientCompany: String
    public let recipientAddress: String?
    public let subject: String
    public let greeting: String?
    public let body: [String]
    public let closing: String?
    public let signatureName: String?

    public init(
        senderName: String,
        senderContact: CvContact,
        date: String,
        recipientName: String? = nil,
        recipientTitle: String? = nil,
        recipientCompany: String,
        recipientAddress: String? = nil,
        subject: String,
        greeting: String? = nil,
        body: [String] = [],
        closing: String? = nil,
        signatureName: String? = nil
    ) {
        self.senderName = senderName
        self.senderContact = senderContact
        self.date = date
        self.recipientName = recipientName
        self.recipientTitle = recipientTitle
        self.recipientCompany = recipientCompany
        self.recipientAddress = recipientAddress
        self.subject = subject
        self.greeting = greeting
        self.body = body
        self.closing = closing
        self.signatureName = signatureName
    }

    /// Blank-not-just-nil, matching the C# `IsNullOrWhiteSpace` checks: a greeting
    /// of "   " must fall through to the derived one, not print as whitespace.
    private static func filled(_ s: String?) -> String? {
        guard let s, !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return s
    }

    /// The greeting to print: the explicit one, else "Dear <name>,", else the
    /// formal fallback.
    public var effectiveGreeting: String {
        if let g = Self.filled(greeting) { return g }
        if let n = Self.filled(recipientName) { return "Dear \(n)," }
        return "Dear Sir or Madam,"
    }

    public var effectiveClosing: String { Self.filled(closing) ?? "Yours sincerely," }

    public var effectiveSignature: String { Self.filled(signatureName) ?? senderName }

    public static func minimal(
        sender: String, contact: CvContact, date: String, company: String, subject: String
    ) -> CoverLetter {
        CoverLetter(senderName: sender, senderContact: contact, date: date,
                    recipientCompany: company, subject: subject)
    }
}

// MARK: - Invoice
//
// NAMED InvoiceDocument, NOT Invoice. Swift is one module where C# has
// namespaces, and CircleAI.Commerce.Finance already owns the bare name for the
// financial RECORD - customer id, lines, currency. This one is the printable
// artifact, so it takes the same suffix as its siblings CvDocument and
// ReportDocument instead of fighting for the word. The C# file is Invoice.cs.

public struct InvoiceParty: Sendable, Equatable {
    public let name: String
    public let address: String?
    public let email: String?
    public let phone: String?
    public let taxNumber: String?

    public init(name: String, address: String? = nil, email: String? = nil,
                phone: String? = nil, taxNumber: String? = nil) {
        self.name = name
        self.address = address
        self.email = email
        self.phone = phone
        self.taxNumber = taxNumber
    }
}

public struct InvoiceLineItem: Sendable, Equatable {
    public let description: String
    public let quantity: Decimal
    public let unitPrice: Decimal

    public init(description: String, quantity: Decimal, unitPrice: Decimal) {
        self.description = description
        self.quantity = quantity
        self.unitPrice = unitPrice
    }

    public var lineTotal: Decimal { quantity * unitPrice }
}

public struct InvoiceDocument: DocumentModel, Equatable {
    public let from: InvoiceParty
    public let to: InvoiceParty
    public let invoiceNumber: String
    public let issueDate: String
    public let dueDate: String
    public let lineItems: [InvoiceLineItem]
    public let vatPercent: Decimal
    public let currencyCode: String
    public let paymentNote: String?

    public init(
        from: InvoiceParty,
        to: InvoiceParty,
        invoiceNumber: String,
        issueDate: String,
        dueDate: String,
        lineItems: [InvoiceLineItem] = [],
        vatPercent: Decimal = 0,
        currencyCode: String = "ZAR",
        paymentNote: String? = nil
    ) {
        self.from = from
        self.to = to
        self.invoiceNumber = invoiceNumber
        self.issueDate = issueDate
        self.dueDate = dueDate
        self.lineItems = lineItems
        self.vatPercent = vatPercent
        self.currencyCode = currencyCode
        self.paymentNote = paymentNote
    }

    /// Two places, HALF-UP - the C# passes `MidpointRounding.AwayFromZero`
    /// explicitly. Foundation's default is bankers' rounding, which would send
    /// 2.125 to 2.12 and put a cent between this port and the reference on every
    /// invoice that lands on a midpoint.
    public static func round2(_ value: Decimal) -> Decimal {
        var input = value
        var result = Decimal()
        NSDecimalRound(&result, &input, 2, .plain)
        return result
    }

    /// Each line is rounded BEFORE summing, exactly as the C# does. Summing first
    /// and rounding once gives a different total on some baskets, and the invoice
    /// has to agree with the line items a person can add up by hand.
    public var subtotal: Decimal {
        Self.round2(lineItems.reduce(Decimal(0)) { $0 + Self.round2($1.lineTotal) })
    }

    public var vatAmount: Decimal { Self.round2(subtotal * vatPercent / 100) }

    public var total: Decimal { subtotal + vatAmount }

    public static func minimal(
        from: InvoiceParty, to: InvoiceParty, number: String, issueDate: String, dueDate: String
    ) -> InvoiceDocument {
        InvoiceDocument(from: from, to: to, invoiceNumber: number, issueDate: issueDate, dueDate: dueDate)
    }
}

// MARK: - Report

public struct ReportTable: Sendable, Equatable {
    public let columns: [String]
    public let rows: [[String]]
    public let caption: String?

    public init(columns: [String], rows: [[String]], caption: String? = nil) {
        self.columns = columns
        self.rows = rows
        self.caption = caption
    }
}

public struct ReportSection: Sendable, Equatable {
    public let heading: String
    public let paragraphs: [String]?
    public let bullets: [String]?
    public let table: ReportTable?

    public init(heading: String, paragraphs: [String]? = nil,
                bullets: [String]? = nil, table: ReportTable? = nil) {
        self.heading = heading
        self.paragraphs = paragraphs
        self.bullets = bullets
        self.table = table
    }
}

public struct ReportDocument: DocumentModel, Equatable {
    public let title: String
    public let subtitle: String?
    public let author: String?
    public let date: String?
    public let sections: [ReportSection]

    public init(title: String, subtitle: String? = nil, author: String? = nil,
                date: String? = nil, sections: [ReportSection] = []) {
        self.title = title
        self.subtitle = subtitle
        self.author = author
        self.date = date
        self.sections = sections
    }

    public static func minimal(title: String) -> ReportDocument {
        ReportDocument(title: title)
    }
}
