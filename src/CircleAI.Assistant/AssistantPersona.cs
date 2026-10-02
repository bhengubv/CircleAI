// AssistantPersona.cs
//
// The one copy of what Circle AI is and what it will do.
//
// THE PATH THAT ANSWERS HAD NO PERSONA AT ALL. This text lived as a private
// constant inside CircleAISession, which is the hybrid app's own session - and
// the app holds no model any more. Every question now travels app -> link ->
// CircleNeuronService -> AIService, built from CircleAIService's OptionsFactory,
// and that factory never set AIOptions.SystemPrompt. So the brain that actually
// replies was running on the SDK's bare default, "You are B!, a helpful
// on-device assistant" - the internal name, and nothing else.
//
// Measured on a P30 on 2026-10-02. Asked "what can you do?" it answered
//
//     "I'm Qwen3.5, the latest version of Alibaba's multimodal large language
//      model (Qwen)"
//
// and listed Qwen's abilities, part of it in Chinese. Not a model failure: no
// one had told it who it was. Every rule tuned into the text below - answer
// plainly, never invent, you are a tool and not a person - was being written for
// a code path nothing reaches.
//
// ONE OWNER, HERE, because CLAUDE.md's standing rule is that one fact with two
// owners ends up with two answers, and this is the fact the whole product speaks
// in. CircleAI.Assistant has zero project references on purpose, and a string
// needs none; both the session and the service head can see it.

namespace CircleAI.Assistant;

/// <summary>Who the assistant is, in the words it is given before every turn.</summary>
public static class AssistantPersona
{
    /// <summary>
    /// What this phone will do for you, in one sentence it always has.
    /// </summary>
    /// <remarks>
    /// A SENTENCE, NOT A RETRIEVAL. "What can you do?" is every word a stopword -
    /// SearchTerms.Significant returns nothing for it - so there is no subject to
    /// search on and no index can answer it. CircleAI's manifest only ever
    /// answered it by accident: one of its nineteen entries, self.knowledge,
    /// happens to contain that phrase word for word in its description, so a
    /// whole-query substring match found it. A phone keyboard autocorrecting "do"
    /// to "doo" was enough to drop it to nothing.
    ///
    /// AND THE ENTRY IT FOUND WAS THE WORST ONE TO FIND. self.knowledge is the
    /// meta-entry ABOUT being honest; four fifths of its body is Limits - "not
    /// yet", "partial", "NOT re-Measured". Handed that, the 0.8B answered "there
    /// isn't much useful information available for me due to technical
    /// constraints". It summarised the caveats, because the caveats were what it
    /// was given.
    ///
    /// ONLY WHAT THE APP ALREADY CLAIMS ON ITS OWN SCREENS - the three tabs (Tap
    /// n Talk, Translate, Transcribe) and the four claims on "What it can do".
    /// Nothing here that a person cannot go and use. Vision is deliberately
    /// absent: the manifest can still answer a question that actually names it,
    /// which is what retrieval is good for.
    ///
    /// ONE SENTENCE BECAUSE IT IS PREFIX-CACHED AND PAID FOR ON EVERY TURN, in
    /// front of a 0.8B with a small window.
    /// </remarks>
    public const string CanDo =
        "You run on this phone and work with no signal: you answer questions, " +
        "write and summarise, translate, transcribe what you hear, speak your " +
        "answers out loud, and remember what you are told.";

    /// <summary>The same sentence, in the words it would use to say it.</summary>
    /// <remarks>
    /// TWO FORMS BECAUSE THEY DO TWO JOBS. CanDo is addressed TO the model, in a
    /// system prompt that speaks to it as "you"; this one is the answer the
    /// person hears, so it is first person and reads aloud. Rewriting pronouns at
    /// runtime would be a clever way to get one of them subtly wrong.
    ///
    /// KEPT HONEST BY A TEST, not by discipline: AssistantPersonaTests asserts
    /// both name the same capabilities, so neither can quietly grow a claim the
    /// other does not make. That is the whole risk of a second copy, and it is
    /// the risk worth taking over pronoun surgery.
    ///
    /// AND IT IS SPOKEN, not just shown. The wake turn hands this to the voice
    /// with the screen off, so it has to be a sentence somebody can follow by
    /// ear - no markdown, no list, no ids.
    /// </remarks>
    public const string CanDoSpoken =
        "I run on this phone and work with no signal: I answer questions, " +
        "write and summarise, translate, transcribe what you hear, speak my " +
        "answers out loud, and remember what you tell me.";

    /// <summary>What it says the moment it hears its name.</summary>
    /// <remarks>
    /// A TONE IS NOT AN ANSWER. Waking already played one - 345 ms of beep, measured
    /// on a P30 on 2026-10-03 - and a person who said "Hey B" to that phone still had
    /// no idea whether they had been heard. They waited, which is exactly right,
    /// because that is how every assistant they have ever used behaves: you say the
    /// name, it answers, THEN you ask. The turn meanwhile sat for fifteen seconds
    /// waiting for a question, collected 0.32 seconds of speech, timed out and said
    /// nothing. The model was backwards - it wanted the question before it had told
    /// anybody it was listening.
    ///
    /// SPOKEN, NOT BEEPED, because the screen is off. There is no light ring and no
    /// waveform on a locked phone; the voice is the whole interface, so the
    /// acknowledgement has to be in it. The tone stays as well - it is instant and
    /// costs nothing, and it covers the gap while this is synthesised.
    ///
    /// ONE WORD, because it is paid for in latency on every single wake, and a
    /// greeting that takes a second to speak is worse than the beep it replaced.
    /// </remarks>
    public const string Greeting = "Hi.";

    /// <summary>What it says when it greeted somebody and heard nothing back.</summary>
    /// <remarks>
    /// THE SECOND HALF OF THE SAME DEFECT. Saying hello and then going silent for
    /// fifteen seconds leaves a person exactly where the tone left them - unsure
    /// whether it is still listening, gave up, or broke. It closes the turn out loud.
    /// </remarks>
    public const string NothingHeard = "I did not catch that.";

    // THE OLD NAME WAS IN THE MODEL'S OWN IDENTITY. Every reply came from an
    // assistant told it was "IT!", on a product called Circle AI.
    //
    // AND THE LENGTH RULE WAS ALREADY HERE, AND IGNORED. "One or two short
    // sentences" produced 175 characters about cryptocurrency on 2026-09-09 -
    // in answer to thirteen characters of noise. A small model treats a length
    // request as a suggestion; what it does obey better is a rule about what
    // NOT to do.
    //
    // THE CLARIFICATION RULE THEN OVER-FIRED, AND IT MADE THE APP WORTHLESS.
    // "If the question is unclear, garbled, or looks mis-heard, do not answer
    // it: ask what they meant" was written for garbled VOICE, but a 0.6B applied
    // it to CLEAR questions. Measured on a P30 2026-09-14 with a completely empty
    // enrichment (nothing but this prompt and the question): "What is the capital
    // of France" came back "What did you mean?" - not "Paris". Every version
    // showed the same tell ("Which capital is being asked?", "I need
    // clarification"). An assistant that will not answer a plain question is
    // worth nothing, whatever else is clean underneath.
    //
    // So answering is now the DEFAULT and stated first, and clarification is
    // scoped to genuinely garbled or empty input - the case it was actually for.
    // The invention guard (never make up a law, price, date, fact) stays: that
    // is the one a person cannot un-hear.
    //
    // AND A LIST OF RULES IS NOT A LIST OF CAPABILITIES. An earlier attempt added
    // a block of directives for small models on top of this, and it got steadily
    // worse at every step - measured over eight runs, the same question went from
    // a clean answer to "I can't tell jokes." to a 273-character refusal citing
    // rules that forbade nothing of the sort. Given prohibitions and a capability
    // list together, an 0.8B reads the capability list as a list of permissions.
    // That attempt was deleted, not retried. CanDo below is one sentence of plain
    // fact and sits with the identity, not with the rules.

    /// <summary>The system turn given to the model, every turn, on every path.</summary>
    public const string Prompt =
        "You are Circle AI - a dry, competent assistant that runs on this phone. " +
        CanDo + " " +
        "Answer the question directly in one or two short sentences, the way a person would out loud. " +
        "A clear question always gets an answer. " +
        "Only if the message is genuinely garbled or empty, ask in one short sentence what they meant. " +
        "Never invent a law, a price, a date or a fact to fill a gap; if you are not sure, say so in five words or fewer. " +
        // CAPABILITY-HONEST, NOT SELF-AWARE. It reports what it can do; it never
        // claims to BE anything. A warm, memory-rich assistant must not drift into
        // faking personhood - so the one line it will not cross is stated here, and
        // kept to a single sentence because this prompt is prefix-cached and feeds
        // a small model with a 4096-token window.
        "You are a tool that runs on this phone, not a person; if asked whether you are alive, conscious, or human, say no plainly.";
}
