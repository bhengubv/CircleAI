// IWakePhrases.cs
//
// What you say to wake the phone, in the language the phone is being used in.
//
// THE PHRASE IS NOT A LANGUAGE CHOICE. That was the shape this replaced: a wake
// phrase language sitting next to the app language, free to disagree with it, so
// the settings screen would happily let somebody run the app in English and wake
// it with ビーさん. Nobody wants that combination and no screen should offer it.
//
// So the language comes from AppSettings.Language and this contract answers the
// two questions that are genuinely open once it is known:
//
//   WHICH PHRASE, because a language can have more than one. Japanese carries
//   ビーさん, ビーさま and Bee san; picking one silently, as the app used to, means
//   the phone answers to a name nobody told the owner.
//
//   AND IF THERE IS NONE, what then. Seventy of the seventy-five languages the
//   app speaks have no wake phrase, and the old screen let you choose them anyway
//   and went on listening for "Hey B". The honest answer is to say so and let the
//   owner add one - it is their phone and their language, and the engine can
//   judge a typed phrase well enough to warn them before they commit to it.
//
// The judging is not advisory politeness. Fewer than four tokens does not survive
// a room - three-token "Hey B" was heard once in ten through air where a
// four-token phrase was heard twelve times out of twelve - and a phrase another
// phrase starts with can never fire at all. Those are measurements, and this
// contract exists so they reach the person at the moment they type, rather than
// three weeks later when the phone will not answer.

namespace CircleAI.Samples.It;

/// <summary>How well a phrase is expected to work.</summary>
public enum WakePhraseQuality
{
    /// <summary>Nothing to say against it.</summary>
    Good,

    /// <summary>Usable, with a caveat the owner should hear.</summary>
    Caution,

    /// <summary>It cannot work; <see cref="WakePhraseOption.Advice"/> says why.</summary>
    Unusable,
}

/// <summary>One phrase that could wake the phone.</summary>
/// <param name="Text">What you say.</param>
/// <param name="Chosen">Whether this is the one currently listened for.</param>
/// <param name="BuiltIn">
/// Whether it shipped with the app rather than being typed by the owner. Shown
/// because a phrase somebody added themselves is theirs to remove, and one that
/// came with the app is not going anywhere.
/// </param>
/// <param name="Quality">How well it is expected to work.</param>
/// <param name="Advice">
/// Plain language for the owner, empty when there is nothing to say. This is
/// where the measurements come out: too short, too ordinary, or shadowed by
/// another phrase.
/// </param>
public sealed record WakePhraseOption(
    string Text,
    bool Chosen,
    bool BuiltIn,
    WakePhraseQuality Quality,
    string Advice);

/// <summary>What happened when somebody offered a new phrase.</summary>
/// <param name="Added">Whether it is now in the list.</param>
/// <param name="Quality">How well it is expected to work.</param>
/// <param name="Advice">Why, in the owner's language rather than the engine's.</param>
/// <remarks>
/// A CAUTION STILL ADDS. It is the owner's phone and there are good reasons to
/// accept a weak phrase; what nobody should do is discover the trade by accident.
/// Only <see cref="WakePhraseQuality.Unusable"/> refuses.
/// </remarks>
public sealed record WakePhraseResult(bool Added, WakePhraseQuality Quality, string Advice);

/// <summary>The wake phrases available in a given language.</summary>
public interface IWakePhrases
{
    /// <summary>
    /// Every phrase this language can be woken with, best first.
    /// </summary>
    /// <remarks>
    /// EMPTY IS A REAL AND COMMON ANSWER - seventy of seventy-five languages -
    /// and the screen must say so rather than fall back to English behind the
    /// owner's back, which is what the app did before.
    /// </remarks>
    Task<IReadOnlyList<WakePhraseOption>> ForAsync(string language, CancellationToken ct = default);

    /// <summary>Judge a phrase without adding it, so the screen can warn as it is typed.</summary>
    Task<WakePhraseResult> CheckAsync(string language, string phrase, CancellationToken ct = default);

    /// <summary>Add a phrase for this language, unless it cannot work at all.</summary>
    Task<WakePhraseResult> AddAsync(string language, string phrase, CancellationToken ct = default);

    /// <summary>Listen for this phrase from now on.</summary>
    Task ChooseAsync(string language, string phrase, CancellationToken ct = default);

    /// <summary>Remove a phrase the owner added. Built-in phrases stay.</summary>
    Task RemoveAsync(string language, string phrase, CancellationToken ct = default);
}
