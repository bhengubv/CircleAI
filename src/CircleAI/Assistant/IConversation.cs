// IConversation.cs
//
// The app as a conversation rather than as a demo.
//
// THIS IS THE GAP THE PARITY AUDIT FOUND. SpokenReply, VoiceTurn, HandsFree,
// Earcon, the greeting cycle and the language read-out are not six missing
// features - they are one: the phone listening and answering out loud. A chat box
// that types back is a demo of a language model; this is what makes it an
// assistant somebody can use without reading.

namespace CircleAI.Assistant;

/// <summary>What a voice turn is doing right now.</summary>
public enum TurnPhase
{
    /// <summary>Nothing happening.</summary>
    Idle,

    /// <summary>Microphone open, waiting for speech.</summary>
    Listening,

    /// <summary>Heard something, working on it.</summary>
    Thinking,

    /// <summary>Answering out loud.</summary>
    Speaking,
}

/// <summary>Where a voice turn has got to.</summary>
/// <param name="Phase">Which part of the exchange.</param>
/// <param name="Level">
/// Microphone level, 0 to 1, while listening. A REAL level or zero - never an
/// invented one, because the mark scales its arcs with this and a fake meter lies
/// about the one thing the person is trying to find out.
/// </param>
/// <param name="Heard">What it understood, once it has.</param>
/// <param name="Reply">The answer so far, as it streams.</param>
/// <param name="Language">
/// The language the turn was spoken in, for the read-out. NOT a setting: it
/// reports what happened, and stays empty until a turn has actually been heard -
/// announcing a language before anybody has spoken is a claim the phone has not
/// earned.
/// </param>
/// <param name="Detail">Anything worth saying, including why it stopped.</param>
public sealed record TurnState(
    TurnPhase Phase,
    double Level = 0,
    string? Heard = null,
    string? Reply = null,
    string? Language = null,
    string? Detail = null);

/// <summary>Runs a spoken exchange: listen, think, answer aloud.</summary>
public interface IConversation
{
    /// <summary>Whether a spoken turn can run at all on this head.</summary>
    Task<BrainState> StateAsync(CancellationToken ct = default);

    /// <summary>
    /// Open everything a turn needs, now, so the first one is not the slow one.
    /// </summary>
    /// <remarks>
    /// THE FIRST TURN PAID FOR ALL OF THIS AND IT IS THE TURN THAT DECIDES. On a
    /// P30 the first decode took ELEVEN SECONDS against under two for the next -
    /// the transcriber opens its model on first use - and the first spoken reply
    /// logs "(INCLUDING model open)" for the same reason. So somebody presses the
    /// circle, having been shown a screen that said it was ready, and watches
    /// nothing happen for eleven seconds.
    /// <para>
    /// It belongs HERE rather than on a probe because this is the object that
    /// caches the engines: a warm-up that opened its own copy would heat
    /// something nobody uses and leave the real path just as cold.
    /// </para>
    /// <para>
    /// Returns what is now open, or why it is not. Safe to call more than once -
    /// the second call finds everything already warm and says so quickly.
    /// </para>
    /// </remarks>
    Task<string> PrepareAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// One turn: open the microphone, hear a question, answer it out loud.
    /// </summary>
    /// <remarks>
    /// Reports every phase, because the mark on the home screen IS the progress
    /// indicator and it has a different motion for each: listening scales with
    /// your voice, thinking travels outward, speaking fires in sequence.
    /// </remarks>
    Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default);

    /// <summary>
    /// Open the microphone and write down what was said. Nothing else.
    /// </summary>
    /// <remarks>
    /// DICTATION IS NOT A CONVERSATION, and the CV screen was using one to do it.
    /// Its "Say it" button called TurnAsync - listen, think, answer aloud - so
    /// speaking your own name into your own CV required the 548 MB answering
    /// model, and the screen reported the ANSWERING model as the thing missing.
    /// Somebody would have downloaded half a gigabyte and found the button still
    /// did not work, because what it actually needs is the ears.
    /// <para>
    /// Worse than the message: a full turn would have taken the name as a
    /// question, thought about it, and said something back - when the only thing
    /// wanted was the words.
    /// </para>
    /// <para>
    /// Returns what was heard, or null when nothing was. The reason lands on
    /// <see cref="TurnState.Detail"/> like every other refusal.
    /// </para>
    /// </remarks>
    /// <param name="language">
    /// What the speaker is speaking, when the caller knows - the interpreter
    /// does, because each half of that screen owns a language and prints it on
    /// its own microphone button. Left null the engine guesses, and a small
    /// model guesses English: Japanese spoken into the Japanese half came back
    /// written down as English.
    /// </param>
    Task<string?> DictateAsync(
        IProgress<TurnState> updates, CancellationToken ct = default, string? language = null);

    /// <summary>
    /// Take down everything said until stopped, then read the whole thing again.
    /// </summary>
    /// <remarks>
    /// A MEETING IS NOT A SEQUENCE OF DICTATIONS. Looping <see cref="DictateAsync"/>
    /// works and is what the transcribe screen did, but it opens and closes the
    /// microphone once per sentence, keeps nothing, and gives each sentence to the
    /// recogniser with no knowledge of the one before it.
    /// <para>
    /// A session holds the microphone open for the whole meeting, cuts at the
    /// silences between sentences, and appends. Each update costs one sentence
    /// rather than everything said so far - which is the difference between
    /// something that works for a minute and something that works for an hour.
    /// </para>
    /// <para>
    /// AND IT READS IT AGAIN AT THE END. The session keeps its own audio, so the
    /// last thing it does is put the entire recording through in one pass and
    /// replace the pieced-together text. A piece cut at a silence was decoded
    /// with nothing after it; read whole, every word has both its sides. The
    /// audio is dropped when the session ends, which is what the screens mean by
    /// "nothing is kept".
    /// </para>
    /// <para>
    /// Reports each piece as it lands, then once more with <c>Final</c> set. A
    /// head that cannot do this - a browser has no microphone it may hold - says
    /// so on <see cref="TurnState.Detail"/> and returns what it has.
    /// </para>
    /// </remarks>
    /// <param name="silenceMs">
    /// How long a gap ends a piece. It cannot be one number for every job: a
    /// meeting wants seconds because people pause to think, and a question wants
    /// about one because the same gap is somebody waiting for an answer.
    /// </param>
    Task<string> SessionAsync(
        IProgress<TurnState> updates, CancellationToken ct = default,
        string? language = null, double silenceMs = 5000);

    /// <summary>Write down a recording that already exists.</summary>
    /// <param name="path">The audio file.</param>
    /// <param name="language">BCP-47 code, or <c>null</c> to let the engine detect.</param>
    /// <param name="progress">
    /// Fraction done, 0 to 1. NOT OPTIONAL IN PRACTICE: transcription runs at
    /// about real time on a P30, so a forty-minute recording is a forty-minute
    /// wait, and a wait with no number is indistinguishable from a hang.
    /// </param>
    /// <param name="ct">Cancels between chunks, so stopping costs one chunk.</param>
    /// <returns>
    /// What was said, when, and - where a speaker model is present - by whom.
    /// </returns>
    /// <remarks>
    /// THE MICROPHONE WAS THE ONLY DOOR. DictateAsync and SessionAsync are both
    /// live capture, so a screen had no way to hand over a recording somebody
    /// already had - a voice memo, an interview, an episode - which is most of
    /// what people reach for a transcriber to do. The library could do it for
    /// some time before anything could ask.
    /// <para>
    /// A head that cannot read files - a browser tab - returns
    /// <see cref="Transcript.Nothing"/> rather than throwing, the same way it
    /// declines a microphone it may not hold.
    /// </para>
    /// <para>
    /// NON-WAV NEEDS A PLATFORM DECODER. A voice memo is .m4a and decoding one
    /// means a codec, so a build without <c>IAudioDecoder</c> wired says so in a
    /// sentence about the build rather than failing with a parse error about
    /// RIFF headers, which tells the person nothing about their perfectly good
    /// recording.
    /// </para>
    /// </remarks>
    Task<Transcript> TranscribeFileAsync(
        string path,
        string? language = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Say this in another language.</summary>
    /// <param name="text">What was said.</param>
    /// <param name="fromTag">BCP-47 of the language it is in.</param>
    /// <param name="toTag">BCP-47 of the language it should come back in.</param>
    /// <param name="ct">Cancels the turn.</param>
    /// <returns>The translation only — no preamble, no explanation.</returns>
    /// <remarks>
    /// THE ENGINE EXISTED AND NOTHING CALLED IT. CircleAI.Languages.Translation
    /// has had ITranslationEngine and LlmTranslationEngine for a long time with
    /// ZERO consumers anywhere in src or samples, while the Translate screen
    /// built its own prompt against the raw brain. Two owners of one fact, and
    /// the screen's version was the better of the two — it named the languages
    /// where the engine used tags.
    /// <para>
    /// So the engine took the screen's prompt and this is the door. A head asks
    /// for a translation; it does not compose one, because composing one is
    /// where "give only the translation" gets forgotten and the model starts
    /// ANSWERING the sentence instead — in front of somebody at a hospital desk.
    /// </para>
    /// </remarks>
    Task<string> TranslateAsync(
        string text, string fromTag, string toTag, CancellationToken ct = default);

    /// <summary>Render a transcript as a subtitle file.</summary>
    /// <remarks>
    /// ON THE CONVERSATION RATHER THAN ON <see cref="Transcript"/> so there stays
    /// ONE owner of the format. The rendering lives beside the transcriber in
    /// CircleAI.Voice, which this assembly deliberately cannot see; a copy here
    /// would be a second owner of a format whose two dialects already differ by
    /// a single punctuation mark that no player complains about.
    /// </remarks>
    string AsSubtitles(Transcript transcript, SubtitleFormat format = SubtitleFormat.SubRip);

    /// <summary>Say something aloud, without listening first.</summary>
    /// <remarks>
    /// What the chat screen's speaker control uses, and the greeting the circle
    /// gives on a phone that cannot converse yet: pressing it must DO something,
    /// because "nothing happens" is indistinguishable from "broken".
    /// </remarks>
    Task SayAsync(string text, string? languageTag = null, CancellationToken ct = default);

    /// <summary>
    /// The person said this - whether they spoke it or typed it.
    /// </summary>
    /// <remarks>
    /// THERE ARE TWO DOORS AND THE MEMORY HAS TO SIT ACROSS BOTH. Speaking goes
    /// through TurnAsync and typing goes straight to the brain, so a memory
    /// wired to one of them remembers half a person. It was wired to the voice
    /// path first, which meant anybody who typed was talking to something that
    /// forgot them.
    ///
    /// Not at the brain either: the job-spec tailor and the interpreter ask the
    /// brain constructed prompts, and those are not anybody's words.
    ///
    /// It never throws and never keeps the caller waiting. Nothing about
    /// remembering is worth delaying an answer.
    /// </remarks>
    Task HeardAsync(string said, CancellationToken ct = default);

    /// <summary>Answer a question about an image.</summary>
    Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default);
}
