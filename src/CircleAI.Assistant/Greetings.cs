// Greetings.cs
//
// What the phone says when you press the circle, in order.
//
// DELIBERATELY NOT ENGLISH FIRST. The point being made is that this thing speaks
// languages other assistants do not, so the very first sound it makes should be
// one of them. isiZulu leads because the eleven-language South African voice is
// the one that exists nowhere else.
//
// THREE SEPARATE OPINIONS ABOUT THIS EXISTED AT ONCE, which is why the list is
// here now rather than in a screen:
//
//   one head           zu, sw, yo, hi, ar, pt      six, with hardcoded phrases
//   the other head     zu, af, st, sw, en          five, tags only
//   Welcome.Languages  zu, xh, af, st, sw, en      six, a different six again
//
// They overlap on two entries. Nothing about the product changed between them;
// they were simply written at different times by whoever was in that file, and
// no two agree on what this app leads with.
//
// THE ORDER IS THE PRODUCT'S OPINION ABOUT ITSELF and belongs in one place. The
// PHRASES are not here: IVoiceHost.SpeakAsync(tag) speaks a checked greeting
// from the catalogue, every one of them verified by somebody who speaks it, and
// a second copy typed into a source file is how a demo comes to mispronounce an
// invented sentence at a native speaker.

namespace CircleAI.Assistant;

/// <summary>The languages the circle greets you in, in the order it uses.</summary>
public static class Greetings
{
    /// <summary>
    /// The carousel, in order.
    /// </summary>
    /// <remarks>
    /// SIX, AND WHY EACH IS THERE. isiZulu and Sesotho for the South African
    /// voice that exists nowhere else; Kiswahili for the continent beyond it;
    /// Afrikaans because it is the other language of the room this was built in;
    /// Yorùbá as the most-spoken language no assistant offers. English LAST, so
    /// it is the one you hear only if you keep pressing.
    /// </remarks>
    public static IReadOnlyList<string> Carousel { get; } =
        ["zu", "st", "af", "sw", "yo", "en"];

    /// <summary>The next language to greet in, given where the carousel is.</summary>
    /// <remarks>
    /// AN INDEX RATHER THAN AN ITERATOR, because a screen holds its position
    /// across renders and Blazor rebuilds the component around it.
    /// </remarks>
    public static string At(int index) =>
        Carousel[((index % Carousel.Count) + Carousel.Count) % Carousel.Count];

    /// <summary>Where the carousel goes after this one.</summary>
    public static int Next(int index) => (index + 1) % Carousel.Count;
}
