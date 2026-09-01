package com.bhengubv.circleai.memory

import java.time.Instant
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MemoryAtomTest {

    @Test
    fun anAtomIsCurrentUntilSomethingSupersedesIt() {
        val a = MemoryAtom(text = "Use dotnet build, not adb push.")
        assertTrue(a.isCurrent)
        assertFalse(a.copy(supersededBy = UUID.randomUUID()).isCurrent)
    }

    @Test
    fun onlyAFACTcanGoSTALE() {
        // A ruling that failed its check is not stale, it is wrong - and a
        // decision that failed is a road found closed, which is still worth
        // remembering. Staleness is about a fact no longer being true.
        val fact = MemoryAtom(kind = AtomKind.FACT, verifiedOk = false)
        assertTrue(fact.isStale)

        assertFalse(MemoryAtom(kind = AtomKind.RULING, verifiedOk = false).isStale)
        assertFalse(MemoryAtom(kind = AtomKind.FACT, verifiedOk = true).isStale)
        // Never checked is not the same as checked and wrong.
        assertFalse(MemoryAtom(kind = AtomKind.FACT).isStale)
    }

    @Test
    fun aFailedOutcomeIsTheSignalRecallPushesToTheTop() {
        assertTrue(MemoryAtom(outcome = DecisionOutcome.FAILED).failed)
        assertFalse(MemoryAtom(outcome = DecisionOutcome.RESOLVED).failed)
        assertFalse(MemoryAtom(outcome = DecisionOutcome.OPEN).failed)
        assertFalse(MemoryAtom().failed)
    }

    @Test
    fun twoAtomsMadeWithoutAnIdAreNotTheSameAtom() {
        // The default id is fresh per atom, or every unsaved atom collides.
        assertTrue(MemoryAtom(text = "x").id != MemoryAtom(text = "x").id)
    }
}

class SituationTest {

    @Test
    fun theKeyIsVerbAndTargetLowercased() {
        assertEquals("deploy:android", Situation(verb = "Deploy", target = "Android").key)
        assertEquals("deploy", Situation(verb = " Deploy ").key)
        assertEquals("", Situation().key)
    }

    @Test
    fun aSlashDelimitedTargetIsWalkedUpFromSpecificToGENERAL() {
        // A rule filed against the general case has to be found by the specific
        // one. Without this, a rule about deploying to Android is invisible the
        // moment somebody names the phone.
        assertEquals(
            listOf("deploy:android/p30/merlin", "deploy:android/p30", "deploy:android", "deploy"),
            Situation(verb = "deploy", target = "android/p30/merlin").keys,
        )
    }

    @Test
    fun theKeysAreMostSpecificFIRST() {
        // The order is the ranking: the first key that matches is the most
        // specific thing known about this action.
        val keys = Situation(verb = "deploy", target = "android/p30").keys
        assertEquals("deploy:android/p30", keys.first())
        assertEquals("deploy", keys.last())
    }

    @Test
    fun aTargetWithNoVerbHasNoKeysAtAll() {
        // A target on its own does not say what is about to happen, and a key
        // that means "anything to do with android" matches too much to help.
        assertTrue(Situation(target = "android").keys.isEmpty())
    }

    @Test
    fun aLeadingOrTrailingSlashDoesNotProduceAnEmptyKey() {
        val keys = Situation(verb = "deploy", target = "/android").keys
        assertFalse(keys.any { it == "deploy:" }, "produced an empty target key: " + keys)
    }

    @Test
    fun theQueryIsEverythingKnownJoinedForKeywordSearch() {
        val s = Situation(verb = "deploy", target = "android", tool = "dotnet", text = "install fails")
        assertEquals("deploy android dotnet install fails", s.query)
    }

    @Test
    fun blankPartsAreLeftOutOfTheQueryRatherThanDoubleSpacing() {
        assertEquals("deploy android", Situation(verb = "deploy", target = "android", tool = "  ").query)
    }

    @Test
    fun aSituationWithNothingToLookUpIsEmpty() {
        assertTrue(Situation().isEmpty)
        assertTrue(Situation(target = "android").isEmpty)
        // Free text alone is still something to search on.
        assertFalse(Situation(text = "why did the install fail").isEmpty)
        assertFalse(Situation(verb = "deploy").isEmpty)
    }
}

class RecallShapeTest {

    @Test
    fun anEmptyResultIsSharedAndSaysItFoundNothing() {
        assertFalse(RecallResult.empty.any)
        assertEquals(0, RecallResult.empty.considered)
        assertTrue(RecallResult.empty.atoms.isEmpty())
    }

    @Test
    fun aResultWithAtomsSaysSo() {
        val r = RecallResult(listOf(MemoryAtom(text = "x")), emptyList(), 12)
        assertTrue(r.any)
        assertEquals(12, r.considered)
    }

    @Test
    fun theDefaultBudgetIsFiveAtomsAndSixHundredCharacters() {
        // Not arbitrary: it is what fits in front of an action without pushing
        // the action itself out of view.
        assertEquals(5, RecallBudget.default.maxAtoms)
        assertEquals(600, RecallBudget.default.maxCharacters)
    }
}

class AtomCandidateTest {

    private fun candidate(confidence: Double) =
        AtomCandidate(MemoryAtom(text = "x"), confidence, "never", "x")

    @Test
    fun theBarIsEightyPercentAndItIsInclusive() {
        // Above it, recorded; below it, offered. Nothing is superseded on a guess.
        assertTrue(candidate(0.80).certain)
        assertTrue(candidate(0.92).certain)
        assertFalse(candidate(0.79).certain)
        assertFalse(candidate(0.66).certain)
    }
}

class CueExtractorTest {

    private val extractor = CueExtractor()

    private fun episode(said: String, appContext: String? = null) = EpisodicMemoryEntry(
        id = UUID.randomUUID().toString(),
        userId = "u1",
        content = said,
        embedding = FloatArray(0),
        userText = said,
        appContext = appContext,
        recordedAtUtc = Instant.ofEpochSecond(1_782_896_400L),
    )

    private fun extract(said: String, subject: String? = null) =
        extractor.extract(episode(said), subject)

    @Test
    fun aRuleStatedAtTheSTARTofASentenceIsARuling() {
        val out = extract("Never use adb push to install, it keeps the old data.")
        assertEquals(1, out.size)
        assertEquals(AtomKind.RULING, out[0].atom.kind)
        assertEquals("never", out[0].cue)
        assertTrue(out[0].certain)
    }

    @Test
    fun theSameWordINSIDEaSentenceIsNotARuling() {
        // "never" at the start is a rule and nothing else. In the middle it is
        // usually a description, and filing it as a rule puts a stray
        // instruction in front of somebody at the worst moment.
        val out = extract("I have never seen that particular error message before today.")
        assertTrue(out.none { it.cue == "never" }, "matched a mid-sentence never: " + out.map { it.cue })
    }

    @Test
    fun theApostropheLESSformsAreCaughtToo() {
        // That is how people type when they are annoyed, which is exactly when
        // they are stating the rule that was just broken.
        assertEquals("dont", extract("Dont ever push to master without running the tests.")[0].cue)
        assertEquals("we dont", extract("Look, we dont use central APIs in this project at all.")[0].cue)
    }

    @Test
    fun aRoadFoundCLOSEDisRecordedAsAFailedDecision() {
        val out = extract("The incremental install did not work on that MIUI phone.")
        assertEquals(1, out.size)
        assertEquals(AtomKind.DECISION, out[0].atom.kind)
        assertEquals(DecisionOutcome.FAILED, out[0].atom.outcome)
    }

    @Test
    fun aSettledDecisionIsRecordedAsRESOLVED() {
        val out = extract("Lets use the sliding window guard for this rate limit.")
        assertEquals(AtomKind.DECISION, out[0].atom.kind)
        assertEquals(DecisionOutcome.RESOLVED, out[0].atom.outcome)
    }

    @Test
    fun aRulingCarriesNoOutcomeBecauseThereIsNothingToResolve() {
        assertNull(extract("Never use adb push to install, it keeps the old data.")[0].atom.outcome)
    }

    @Test
    fun beingToldAGAINscoresHighest() {
        // The single highest-value thing in a transcript: whatever follows has
        // already cost somebody twice.
        val out = extract("I told you already, the P30 is the only benchmark that counts.")
        assertEquals(AtomKind.RULING, out[0].atom.kind)
        assertTrue(out[0].confidence >= 0.90)
    }

    @Test
    fun aPreferenceIsKeptAsAPreferenceNotAsARule() {
        val out = extract("I prefer the answer first and then the explanation after it.")
        assertEquals(AtomKind.PREFERENCE, out[0].atom.kind)
    }

    @Test
    fun theMOSTSPECIFICcueWinsWhenTwoAreInOneSentence() {
        // Filing it twice makes one complaint look like a pattern.
        val out = extract("I told you that you keep forgetting to uninstall before deploying.")
        assertEquals(1, out.size)
        assertEquals("i told you", out[0].cue)
    }

    @Test
    fun theSENTENCEisKeptWholeRatherThanParaphrased() {
        // Paraphrasing is where extraction starts inventing, and an invented
        // memory comes back with the same confidence as a true one.
        val said = "Never use adb push to install, it keeps the old data"
        assertEquals(said, extract(said + ".")[0].atom.text)
        assertEquals(said, extract(said + ".")[0].quote)
    }

    @Test
    fun aSentenceTooSHORTtoMeanAnythingIsSkipped() {
        // "never mind" and "stop it" carry a cue and no content, and filing them
        // fills the memory with things that match everything and mean nothing.
        assertTrue(extract("Never mind.").isEmpty())
        assertTrue(extract("Stop it.").isEmpty())
        assertTrue(extract("I want that.").isEmpty())
    }

    @Test
    fun aParagraphThatMERELYCONTAINStheWordIsSkipped() {
        // Keeping it would put a page into a recall budget that holds 600
        // characters.
        val long = "Never " + "and so on ".repeat(40)
        assertTrue(long.length > CueExtractor.LONGEST_WORTH_KEEPING)
        assertTrue(extract(long).isEmpty())
    }

    @Test
    fun onlyWhatThePERSONsaidIsRead() {
        // Extracting from the assistant turn lets the thing that was just
        // corrected file its own version of events alongside the correction.
        val e = EpisodicMemoryEntry(
            id = UUID.randomUUID().toString(),
            userId = "u1",
            content = "",
            embedding = FloatArray(0),
            userText = "",
            assistantText = "Never use adb push to install, it keeps the old data.",
            recordedAtUtc = Instant.EPOCH,
        )
        assertTrue(extractor.extract(e).isEmpty())
    }

    @Test
    fun theSameSentenceTwiceInOneTurnIsFiledONCE() {
        val said = "Never use adb push to install, it keeps the old data."
        assertEquals(1, extract(said + " " + said).size)
    }

    @Test
    fun theSubjectIsTAKENnotGUESSED() {
        // A wrong subject key makes an atom findable in the wrong situation and
        // invisible in the right one, which is worse than no key at all.
        assertEquals("deploy", extract("Never use adb push to install, it keeps data.", "deploy")[0].atom.subject)
        // With nothing given, the episode context is used - still not invented.
        val out = extractor.extract(episode("Never use adb push to install, it keeps data.", "android"))
        assertEquals("android", out[0].atom.subject)
        // And with neither, no subject at all.
        assertNull(extract("Never use adb push to install, it keeps data.")[0].atom.subject)
    }

    @Test
    fun theEpisodeTimeIsCarriedOntoTheAtom() {
        // When it was SAID, not when it was filed. A batch import of an old
        // transcript must not look like today.
        assertEquals(Instant.ofEpochSecond(1_782_896_400L), extract("Never use adb push, it keeps data.")[0].atom.recordedAtUtc)
    }

    @Test
    fun severalSentencesInOneTurnEachProduceTheirOwnAtom() {
        val out = extract(
            "Never use adb push to install, it keeps the old data. " +
                "I prefer the answer first and the explanation after it.",
        )
        assertEquals(2, out.size)
        assertEquals(setOf(AtomKind.RULING, AtomKind.PREFERENCE), out.map { it.atom.kind }.toSet())
    }

    @Test
    fun aFullStopInsideAVersionNumberDoesNotSPLITtheSentence() {
        // Only whitespace or the end of the text ends a sentence, or every file
        // name and version cuts a rule in half.
        val out = extract("Never build against net9.0 for this, always use net10.0 here.")
        assertEquals(1, out.size)
        assertContains(out[0].atom.text, "net10.0")
    }

    @Test
    fun aCueMustSitAtAWORDboundary() {
        // "use " inside "abuse the" would file a decision nobody made.
        assertEquals(-1, CueExtractor.position("dont abuse the thing", "use "))
        assertEquals(5, CueExtractor.position("dont use the thing", "use "))
    }

    @Test
    fun normalisingCollapsesWhitespaceCaseAndTrailingPunctuation() {
        // The same sentence typed twice with different punctuation is ONE
        // memory; filing it twice is how a memory starts repeating itself.
        assertEquals(
            CueExtractor.normalise("Never use ADB push!"),
            CueExtractor.normalise("  never   use   adb   push  "),
        )
        assertEquals("never use adb push", CueExtractor.normalise("Never use ADB push."))
    }

    @Test
    fun sentenceSplittingHandlesNewlinesBulletsAndQuestionMarks() {
        val out = CueExtractor.sentences("- First one here\n* Second one there? Third one!")
        assertEquals(listOf("First one here", "Second one there", "Third one"), out)
        assertTrue(CueExtractor.sentences("   ").isEmpty())
    }

    @Test
    fun theExtractorNamesItself() {
        assertEquals("cues", extractor.name)
    }
}
