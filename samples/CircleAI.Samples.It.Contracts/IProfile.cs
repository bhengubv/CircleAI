// IProfile.cs
//
// What the app knows about you.
//
// GATHERED ONCE, USED EVERYWHERE. The CV interview already asks for a name, a
// phone number, where somebody lives, what they do and what they are good at -
// and then buries all of it inside a career database that only the CV screen can
// reach. A letter, an invoice, a form filled in at a clinic and an application to
// a landlord all want the same facts, and today each would have to ask again.
//
// So the profile is its own thing and the CV is one document made FROM it. That
// is also why this screen is on the bar: what the phone knows about you is the
// most personal thing it holds, and being able to see it and change it is the
// other half of promising it never leaves.

namespace CircleAI.Samples.It;

/// <summary>One fact the app holds about the person using it.</summary>
/// <param name="Key">Stable identifier, for reading and writing it back.</param>
/// <param name="Label">What it is, in the words somebody would use.</param>
/// <param name="Value">What is stored, or empty.</param>
/// <param name="Hint">What to say when it is empty.</param>
/// <param name="Multiline">Whether it is a sentence rather than a word.</param>
public sealed record ProfileFact(
    string Key, string Label, string Value, string Hint, bool Multiline = false);

/// <summary>What the app knows about you, and how complete it is.</summary>
/// <param name="Facts">The individual facts, in the order to show them.</param>
/// <param name="Completeness">
/// 0 to 1. Shown so somebody can see what filling in one more line buys them,
/// rather than being nagged.
/// </param>
public sealed record Profile(IReadOnlyList<ProfileFact> Facts, double Completeness);

/// <summary>Reads and writes what the app knows about the person.</summary>
public interface IProfile
{
    /// <summary>Everything held about them right now.</summary>
    Task<Profile> LoadAsync(CancellationToken ct = default);

    /// <summary>Change one fact.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Delete everything the app knows about them.
    /// </summary>
    /// <remarks>
    /// NOT A SETTING, A RIGHT. Every promise this app makes about privacy is
    /// hollow if the person cannot get their own details back out of it, and a
    /// profile that can only be added to is a profile somebody will stop feeding.
    /// </remarks>
    Task ForgetAsync(CancellationToken ct = default);
}
