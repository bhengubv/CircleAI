// Documents.kt
//
// Port of CircleAI.Documents. One engine, four kinds - a CV and an invoice
// differ only in their model and template, never in the pipeline.
//
// C# -> Kotlin notes:
//   * `object Model` on the request becomes the DocumentModel marker interface,
//     so a mismatched kind is mostly a compile-time matter instead of a throw.
//   * `decimal` becomes BigDecimal. The rounding mode is spelled out below.
//   * `ValueTask<DocumentResult>` becomes a suspend function.

package com.bhengubv.circleai.documents

import java.math.BigDecimal
import java.math.RoundingMode

/**
 * What kind of document is being produced.
 *
 * Every kind rides the SAME engine.
 */
enum class DocumentKind { CV, COVER_LETTER, INVOICE, REPORT }

/**
 * Output container format.
 *
 * PDF only for v1 - it is the format a person can open and send from a phone.
 * The enum exists so DOCX/HTML can arrive later without moving the seam.
 */
enum class DocumentFormat { PDF }

/** A typed content model one of the templates can lay out. */
interface DocumentModel

/** A request to render one document. */
data class DocumentRequest(
    val kind: DocumentKind,
    val model: DocumentModel,
    val templateId: String? = null,
    val format: DocumentFormat = DocumentFormat.PDF,
)

/**
 * A rendered document, as bytes.
 *
 * Bytes, not a file path, on purpose: WHERE the document lands is a
 * platform-specific decision the host owns. The engine never touches a filesystem.
 */
class DocumentResult(
    val bytes: ByteArray,
    val mimeType: String,
    val suggestedFileName: String,
) {
    override fun equals(other: Any?): Boolean =
        other is DocumentResult &&
            bytes.contentEquals(other.bytes) &&
            mimeType == other.mimeType &&
            suggestedFileName == other.suggestedFileName

    override fun hashCode(): Int =
        (bytes.contentHashCode() * 31 + mimeType.hashCode()) * 31 + suggestedFileName.hashCode()

    override fun toString(): String =
        "DocumentResult(" + bytes.size + " bytes, " + mimeType + ", " + suggestedFileName + ")"

    companion object {
        /** Builds a PDF result with the standard MIME type. */
        fun pdf(bytes: ByteArray, suggestedFileName: String): DocumentResult =
            DocumentResult(bytes, "application/pdf", suggestedFileName)
    }
}

/** Renders a request to bytes, fully offline and on-device. */
interface DocumentEngine {
    suspend fun render(request: DocumentRequest): DocumentResult

    /** Template ids this engine can render. The first is the default. */
    val availableTemplates: List<String>
}

/**
 * Raised when a request model does not match its kind.
 *
 * The engine validates the type and reports a clear error rather than rendering
 * garbage; this is that error, named.
 */
class DocumentModelMismatch(
    val kind: DocumentKind,
    val received: String,
) : Exception("A " + kind + " document cannot be rendered from a " + received + ".")

// ---------------------------------------------------------------- CV

data class CvContact(
    val email: String? = null,
    val phone: String? = null,
    val location: String? = null,
    val links: List<String>? = null,
)

data class CvExperience(
    val title: String,
    val organisation: String,
    val location: String? = null,
    val startDate: String,
    val endDate: String? = null,
    val highlights: List<String> = emptyList(),
)

data class CvEducation(
    val qualification: String,
    val institution: String,
    val location: String? = null,
    val startDate: String? = null,
    val endDate: String? = null,
    val notes: String? = null,
)

data class CvCertification(
    val name: String,
    val issuer: String? = null,
    val year: String? = null,
)

/** A curriculum vitae. Also a clean JSON target for a model filling one in. */
data class CvDocument(
    val fullName: String,
    val headline: String,
    val contact: CvContact,
    val summary: String? = null,
    val experience: List<CvExperience> = emptyList(),
    val education: List<CvEducation> = emptyList(),
    val skills: List<String> = emptyList(),
    val certifications: List<CvCertification>? = null,
    val languages: List<String>? = null,
) : DocumentModel {
    companion object {
        /** The smallest CV the engine will render: a name, a headline, a way to reach you. */
        fun minimal(fullName: String, headline: String, contact: CvContact): CvDocument =
            CvDocument(fullName, headline, contact)
    }
}

// ------------------------------------------------------- Cover letter

data class CoverLetter(
    val senderName: String,
    val senderContact: CvContact,
    val date: String,
    val recipientName: String? = null,
    val recipientTitle: String? = null,
    val recipientCompany: String,
    val recipientAddress: String? = null,
    val subject: String,
    val greeting: String? = null,
    val body: List<String> = emptyList(),
    val closing: String? = null,
    val signatureName: String? = null,
) : DocumentModel {

    /**
     * The greeting to print: the explicit one, else the named one, else the
     * formal fallback.
     */
    val effectiveGreeting: String
        get() {
            filled(greeting)?.let { return it }
            filled(recipientName)?.let { return "Dear " + it + "," }
            return "Dear Sir or Madam,"
        }

    val effectiveClosing: String get() = filled(closing) ?: "Yours sincerely,"

    val effectiveSignature: String get() = filled(signatureName) ?: senderName

    companion object {
        /**
         * Blank-not-just-null, matching the C# IsNullOrWhiteSpace checks: a
         * greeting of three spaces must fall through to the derived one rather
         * than printing as whitespace.
         */
        private fun filled(s: String?): String? =
            if (s == null || s.isBlank()) null else s

        fun minimal(
            sender: String,
            contact: CvContact,
            date: String,
            company: String,
            subject: String,
        ): CoverLetter = CoverLetter(
            senderName = sender,
            senderContact = contact,
            date = date,
            recipientCompany = company,
            subject = subject,
        )
    }
}

// ----------------------------------------------------------- Invoice
//
// NAMED InvoiceDocument, NOT Invoice, for the same reason the Swift port does:
// com.bhengubv.circleai.commerce.finance already owns the bare name for the
// financial RECORD. This one is the printable artifact, so it takes the same
// suffix as its siblings CvDocument and ReportDocument.

data class InvoiceParty(
    val name: String,
    val address: String? = null,
    val email: String? = null,
    val phone: String? = null,
    val taxNumber: String? = null,
)

data class InvoiceLineItem(
    val description: String,
    val quantity: BigDecimal,
    val unitPrice: BigDecimal,
) {
    val lineTotal: BigDecimal get() = quantity.multiply(unitPrice)
}

data class InvoiceDocument(
    val from: InvoiceParty,
    val to: InvoiceParty,
    val invoiceNumber: String,
    val issueDate: String,
    val dueDate: String,
    val lineItems: List<InvoiceLineItem> = emptyList(),
    val vatPercent: BigDecimal = BigDecimal.ZERO,
    val currencyCode: String = "ZAR",
    val paymentNote: String? = null,
) : DocumentModel {

    /**
     * Each line is rounded BEFORE summing, exactly as the C# does. Summing first
     * and rounding once gives a different total on some baskets, and the invoice
     * has to agree with the line items a person can add up by hand.
     */
    val subtotal: BigDecimal
        get() = round2(lineItems.fold(BigDecimal.ZERO) { acc, item -> acc.add(round2(item.lineTotal)) })

    val vatAmount: BigDecimal
        get() = round2(subtotal.multiply(vatPercent).divide(BigDecimal(100)))

    val total: BigDecimal get() = subtotal.add(vatAmount)

    companion object {
        /**
         * Two places, HALF-UP - the C# passes MidpointRounding.AwayFromZero
         * explicitly. BigDecimal defaults to HALF_EVEN, which would send 2.125 to
         * 2.12 and put a cent between this port and the reference on every
         * invoice that lands on a midpoint.
         */
        fun round2(value: BigDecimal): BigDecimal = value.setScale(2, RoundingMode.HALF_UP)

        fun minimal(
            from: InvoiceParty,
            to: InvoiceParty,
            number: String,
            issueDate: String,
            dueDate: String,
        ): InvoiceDocument = InvoiceDocument(from, to, number, issueDate, dueDate)
    }
}

// ------------------------------------------------------------ Report

data class ReportTable(
    val columns: List<String>,
    val rows: List<List<String>>,
    val caption: String? = null,
)

data class ReportSection(
    val heading: String,
    val paragraphs: List<String>? = null,
    val bullets: List<String>? = null,
    val table: ReportTable? = null,
)

data class ReportDocument(
    val title: String,
    val subtitle: String? = null,
    val author: String? = null,
    val date: String? = null,
    val sections: List<ReportSection> = emptyList(),
) : DocumentModel {
    companion object {
        fun minimal(title: String): ReportDocument = ReportDocument(title)
    }
}
