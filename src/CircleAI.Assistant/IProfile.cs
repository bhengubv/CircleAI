// IProfile.cs
//
// What the app knows about you.
//
// GATHERED ONCE, USED EVERYWHERE. The CV interview asks for a name, a phone
// number, where somebody lives, what they do and what they are good at - and a
// letter, an invoice, a form filled in at a clinic and an application to a
// landlord all want the same facts. So the profile is its own thing and the CV
// is one document made FROM it.
//
// AND IT IS ALL OF IT, NOT THE EASY QUARTER. This contract used to expose five
// identity fields while the store underneath held work history, skills,
// education, certificates and languages as well - which is the exact complaint
// this file was written to make, reproduced by the file making it. The screen
// could not show what the phone knew, so nobody could check it, correct it or
// delete it, and the completeness figure it printed was capped at 50% by
// arithmetic: the four fields on show are worth 9 of the 18 points the career
// store weighs.
//
// This screen is on the bar because what the phone knows about you is the most
// personal thing it holds. Being able to see it and change it is the other half
// of promising it never leaves.

namespace CircleAI.Samples.It;

/// <summary>One fact the app holds about the person using it.</summary>
/// <param name="Key">Stable identifier, for reading and writing it back.</param>
/// <param name="Label">What it is, in the words somebody would use.</param>
/// <param name="Value">What is stored, or empty.</param>
/// <param name="Hint">What to say when it is empty.</param>
/// <param name="Multiline">Whether it is a sentence rather than a word.</param>
public sealed record ProfileFact(
    string Key, string Label, string Value, string Hint, bool Multiline = false);

/// <summary>One thing on a list the app holds - a job, a skill, a certificate.</summary>
/// <param name="Id">Its row in the store, for removing it.</param>
/// <param name="Title">The thing itself: "Forklift driver", "isiZulu".</param>
/// <param name="Detail">
/// The supporting line - where, when, how long - or empty. Kept separate from
/// the title so a list reads as a list rather than as sentences.
/// </param>
public sealed record ProfileEntry(long Id, string Title, string Detail);

/// <summary>A list the app holds about somebody.</summary>
/// <param name="Key">
/// Which store table this is, so removing an entry does not need a lookup table
/// in the UI: "history", "skill", "education", "certification", "language".
/// </param>
/// <param name="Title">What it is called on screen.</param>
/// <param name="Entries">What is on it, in the order to show them.</param>
/// <param name="Nothing">
/// What to say when it is empty. NOT "no items" - a person reading this screen
/// wants to know what would go here and why it is worth having.
/// </param>
public sealed record ProfileSection(
    string Key, string Title, IReadOnlyList<ProfileEntry> Entries, string Nothing);

/// <summary>What the app knows about you.</summary>
/// <param name="Facts">The single facts - name, phone, where they live.</param>
/// <param name="Sections">The lists - work, skills, education, certificates, languages.</param>
/// <param name="Completeness">
/// 0 to 1, weighted by what an employer looks for first rather than by field
/// count. Only honest now that the screen shows everything it counts.
/// </param>
/// <param name="Missing">
/// The one absent thing worth most, in plain words, or empty when there is
/// nothing to say.
/// </param>
/// <remarks>
/// COMPUTED WHERE THE WEIGHTS LIVE. The UI must not work out what matters most -
/// that would be a second copy of the scoring, free to disagree with the bar
/// printed beside it.
/// </remarks>
public sealed record Profile(
    IReadOnlyList<ProfileFact> Facts,
    IReadOnlyList<ProfileSection> Sections,
    double Completeness,
    string Missing);

/// <summary>Reads and writes what the app knows about the person.</summary>
public interface IProfile
{
    /// <summary>Everything held about them right now.</summary>
    Task<Profile> LoadAsync(CancellationToken ct = default);

    /// <summary>Change one fact.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Take one entry off one of the lists.</summary>
    /// <remarks>
    /// REMOVING, NOT EDITING. The lists are gathered by being asked - a job with
    /// its dates and what was achieved in it does not fit in a text box - so the
    /// thing a person needs here is to strike out what is wrong or stale. Adding
    /// happens in the conversation, which is where it can be asked about properly.
    /// </remarks>
    Task RemoveAsync(string section, long id, CancellationToken ct = default);

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
