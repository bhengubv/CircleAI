// Capabilities.cs
//
// What the app can do, in one list, so it can be SHOWN.
//
// THIS FILE EXISTS BECAUSE NOBODY COULD FIND OUT. The repository carries 165
// projects - banking, healthcare, education, legal, agriculture, eldercare - and
// the app surfaced one of them. Somebody with the whole source tree open could
// not have told you it does agriculture. A person holding the phone had no
// chance.
//
// The screen named "What it can do" answered with Talking, Listening, Answering,
// Seeing and Waking: those are MODALITIES, not capabilities. It was a download
// manager wearing the name of the question.
//
// So: one flat, honest list of what this thing helps with, grouped, and dense
// enough to scan. Every entry opens the conversation already pointed at the
// thing, because that is how an assistant works - you ask it, you do not
// navigate to it. Breadth costs no screens.

namespace CircleAI.Samples.It;

/// <summary>One thing the app can help with.</summary>
/// <param name="Title">What it is, in the words somebody would use.</param>
/// <param name="Icon">A glyph, for scanning rather than reading.</param>
/// <param name="Opener">
/// What to ask when it is tapped. A FIRST SENTENCE, not a category: the point of
/// a tile is to get somebody past the blank page, and "Help me with my money" is
/// a question the assistant can answer where "Money" is not.
/// </param>
/// <param name="Route">
/// A screen where one genuinely exists, instead of the conversation. Only a
/// handful do; the rest are answered by asking.
/// </param>
public sealed record Capability(string Title, string Icon, string Opener, string? Route = null);

/// <summary>A section of the grid.</summary>
public sealed record CapabilityGroup(string Title, Capability[] Items);

/// <summary>Everything the app can help with, grouped for a grid.</summary>
public static class Capabilities
{
    /// <summary>The groups, in the order they are shown.</summary>
    /// <remarks>
    /// ORDERED BY WHAT MATTERS ON A CHEAP PHONE IN SOUTH AFRICA, not by what is
    /// most impressive: money and work first, because that is what somebody needs
    /// an assistant for when they cannot afford one. Play is last and is still
    /// there, because an assistant that only does chores is one people stop
    /// opening.
    /// </remarks>
    public static IReadOnlyList<CapabilityGroup> All { get; } =
    [
        new("Money and work",
        [
            new("Your CV", "📄", "Help me build my CV", "career"),
            new("Aim at a job", "🎯", "Help me aim my CV at a job", "job-spec"),
            new("Banking", "🏦", "Help me understand my bank account"),
            new("Budgeting", "💰", "Help me plan my money this month"),
            new("Business", "🧾", "Help me run my small business"),
            new("Invoices", "🧮", "Help me write an invoice"),
            new("Markets", "📈", "Explain what is happening in the markets"),
            new("Buying and selling", "🛒", "Help me sell something"),
        ]),

        new("Health and safety",
        [
            new("Health", "❤️", "I have a health question"),
            new("Mental health", "🧠", "I want to talk about how I am feeling"),
            new("Fitness", "🏃", "Help me get fitter"),
            new("Food", "🍲", "Help me plan meals"),
            new("Safety", "🛡️", "Help me stay safe"),
            new("Older family", "🧓", "Help me care for an older relative"),
        ]),

        new("Family and home",
        [
            new("Family", "👨‍👩‍👧", "Help me with something at home"),
            new("Children", "🧒", "Help me with my children"),
            new("Parenting", "🍼", "I have a parenting question"),
            new("Home", "🏠", "Help me sort something out at home"),
            new("Electricity", "⚡", "Help me with electricity and load shedding"),
            new("Pets", "🐕", "I have a question about my pet"),
        ]),

        new("Learning",
        [
            new("Study help", "📚", "Help me study"),
            new("Languages", "🌐", "Help me with a language", "languages"),
            new("Interpret", "🔤", "Interpret between two languages", "interpret"),
            new("Explain something", "💡", "Explain something to me simply"),
            new("Research", "🔎", "Help me research something"),
        ]),

        new("Documents and making things",
        [
            new("Write something", "✍️", "Help me write something"),
            new("A letter", "✉️", "Help me write a letter"),
            new("A presentation", "📊", "Help me make a presentation"),
            new("Read a picture", "🖼️", "Look at a picture and tell me what it says", "chat"),
        ]),

        new("Getting by",
        [
            new("Legal", "⚖️", "I have a legal question"),
            new("Government", "🏛️", "Help me deal with a government office"),
            new("Community", "🤝", "Help me with something in my community"),
            new("Travel", "🚌", "Help me plan a trip"),
            new("Housing", "🔑", "I have a question about renting or housing"),
            new("Farming", "🌱", "Help me with my crops or livestock"),
        ]),

        new("For enjoyment",
        [
            new("Music", "🎵", "Talk to me about music"),
            new("Sport", "⚽", "Talk to me about sport"),
            new("Stories", "📖", "Tell me a story"),
            new("Games", "🎲", "Play a game with me"),
        ]),
    ];
}
