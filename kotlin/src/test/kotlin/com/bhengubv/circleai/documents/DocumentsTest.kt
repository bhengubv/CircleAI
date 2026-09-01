package com.bhengubv.circleai.documents

import java.math.BigDecimal
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class DocumentContractTest {

    @Test
    fun pdfResultCarriesTheStandardMimeType() {
        val r = DocumentResult.pdf(byteArrayOf(37, 80, 68, 70), "Thabo-Mokoena-CV.pdf")
        assertEquals("application/pdf", r.mimeType)
        assertEquals("Thabo-Mokoena-CV.pdf", r.suggestedFileName)
        assertEquals(4, r.bytes.size)
    }

    @Test
    fun resultsCompareByCONTENTnotByArrayIdentity() {
        // Two separately allocated arrays holding the same bytes are the same
        // document. Kotlin ByteArray equals is identity, which is why this class
        // spells out equals rather than being a data class.
        val a = DocumentResult.pdf(byteArrayOf(1, 2, 3), "x.pdf")
        val b = DocumentResult.pdf(byteArrayOf(1, 2, 3), "x.pdf")
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertTrue(a != DocumentResult.pdf(byteArrayOf(1, 2, 4), "x.pdf"))
    }

    @Test
    fun requestDefaultsToPdfAndTheEngineDefaultTemplate() {
        val req = DocumentRequest(DocumentKind.CV, CvDocument.minimal("A", "B", CvContact()))
        assertEquals(DocumentFormat.PDF, req.format)
        assertNull(req.templateId)
    }

    @Test
    fun mismatchNamesBothTheKindAndWhatArrived() {
        val e = DocumentModelMismatch(DocumentKind.INVOICE, "CvDocument")
        assertEquals(DocumentKind.INVOICE, e.kind)
        assertEquals("CvDocument", e.received)
        assertContains(e.message ?: "", "INVOICE")
        assertContains(e.message ?: "", "CvDocument")
    }

    @Test
    fun everyKindIsRenderableByTheOneEngineSeam() = runTest {
        // The point of the marker interface: one non-generic engine takes all four.
        val engine = RecordingEngine()
        val models: List<Pair<DocumentKind, DocumentModel>> = listOf(
            DocumentKind.CV to CvDocument.minimal("N", "H", CvContact()),
            DocumentKind.COVER_LETTER to CoverLetter.minimal("N", CvContact(), "1 Sep 2026", "Co", "Subj"),
            DocumentKind.INVOICE to InvoiceDocument.minimal(
                InvoiceParty("A"), InvoiceParty("B"), "INV-1", "1 Sep", "30 Sep",
            ),
            DocumentKind.REPORT to ReportDocument.minimal("T"),
        )
        for ((kind, model) in models) {
            val r = engine.render(DocumentRequest(kind, model))
            assertEquals("application/pdf", r.mimeType)
        }
        assertEquals(4, engine.seen.size)
        assertEquals(DocumentKind.entries.toSet(), engine.seen.map { it.kind }.toSet())
    }

    @Test
    fun anEngineRejectsAModelThatDoesNotMatchTheKind() = runTest {
        val engine = StrictEngine()
        val e = assertFailsWith<DocumentModelMismatch> {
            engine.render(DocumentRequest(DocumentKind.INVOICE, ReportDocument.minimal("T")))
        }
        assertEquals(DocumentKind.INVOICE, e.kind)
        assertEquals("ReportDocument", e.received)
    }

    @Test
    fun theFirstAvailableTemplateIsTheDefault() {
        assertEquals("classic", RecordingEngine().availableTemplates.first())
    }

    private class RecordingEngine : DocumentEngine {
        val seen = mutableListOf<DocumentRequest>()
        override val availableTemplates = listOf("classic", "modern")
        override suspend fun render(request: DocumentRequest): DocumentResult {
            seen += request
            return DocumentResult.pdf(byteArrayOf(37), "out.pdf")
        }
    }

    private class StrictEngine : DocumentEngine {
        override val availableTemplates = listOf("classic")
        override suspend fun render(request: DocumentRequest): DocumentResult {
            val ok = when (request.kind) {
                DocumentKind.CV -> request.model is CvDocument
                DocumentKind.COVER_LETTER -> request.model is CoverLetter
                DocumentKind.INVOICE -> request.model is InvoiceDocument
                DocumentKind.REPORT -> request.model is ReportDocument
            }
            if (!ok) {
                throw DocumentModelMismatch(
                    request.kind,
                    request.model::class.simpleName ?: "unknown",
                )
            }
            return DocumentResult.pdf(byteArrayOf(37), "out.pdf")
        }
    }
}
